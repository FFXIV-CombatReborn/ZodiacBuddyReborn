    using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateEgressOutcome
{
    Running,
    Safe,
    Failed,
}

internal sealed class FatePreemptionEgressController
{
    private const double PathfindTimeoutSeconds = 6d;
    private const double MovementProgressTimeoutSeconds = 6d;
    private const double StopRetrySeconds = 1d;
    private const float SafeOutsideMargin = 10f;
    private const float PathTargetMargin = 20f;
    private const float ExtensionDistance = 20f;
    private const float ArrivalDistance = 2.5f;
    private const float ProgressDistance = 1f;
    private const float SuccessfulSegmentDistance = 4f;
    private const float ProjectionVerticalTolerance = 12f;
    private const int MaxCandidateAttempts = 12;

    private static readonly float[] CandidateAngles =
    [
        0f,
        MathF.PI / 4f,
        -MathF.PI / 4f,
        MathF.PI / 2f,
        -MathF.PI / 2f,
        3f * MathF.PI / 4f,
        -3f * MathF.PI / 4f,
    ];

    private EgressState state;
    private StopDisposition stopDisposition;
    private Vector3 fateCenter;
    private float fateRadius;
    private int candidateAttempt;
    private Vector3 destination;
    private Vector3 segmentStart;
    private Vector3 progressAnchor;
    private DateTime stateDeadline;
    private DateTime progressDeadline;
    private DateTime movementStartedAt;
    private bool pathStarted;
    private bool pathOwned;
    private CancellationTokenSource? pathCancellation;
    private Task<List<Vector3>>? pathTask;
    private string stopReason = string.Empty;

    internal bool IsActive { get; private set; }
    internal string Status { get; private set; } = string.Empty;

    internal void Begin(Vector3 center, float radius)
    {
        Reset();
        fateCenter = center;
        fateRadius = Math.Max(1f, radius);
        IsActive = true;
        state = EgressState.Idle;
        Status = "Preparing terrain-aware FATE egress.";
    }

    internal FateEgressOutcome Tick(Vector3 playerPosition, bool inCombat)
    {
        if (!IsActive)
            return FateEgressOutcome.Failed;

        if (state == EgressState.Pathfinding && IsSafe(playerPosition, inCombat))
        {
            BeginStop(StopDisposition.Safe, "combat cleared after leaving the FATE while pathfinding", DateTime.Now);
            return FateEgressOutcome.Running;
        }

        switch (state)
        {
            case EgressState.Idle:
                if (IsSafe(playerPosition, inCombat))
                {
                    IsActive = false;
                    Status = "Outside the filler FATE and combat is clear.";
                    return FateEgressOutcome.Safe;
                }

                if (!StartNextPath(playerPosition))
                {
                    IsActive = false;
                    Status = "No reachable outward egress route could be generated.";
                    return FateEgressOutcome.Failed;
                }
                return FateEgressOutcome.Running;

            case EgressState.Pathfinding:
                TickPathfinding(playerPosition);
                return FateEgressOutcome.Running;

            case EgressState.Moving:
                TickMoving(playerPosition, inCombat);
                return FateEgressOutcome.Running;

            case EgressState.Stopping:
                return TickStopping(playerPosition, inCombat);

            default:
                return FateEgressOutcome.Running;
        }
    }

    internal void Reset()
    {
        if (pathOwned)
            StopOwnedVnavActivity();

        DisposePathRequest();
        state = EgressState.Idle;
        stopDisposition = StopDisposition.None;
        fateCenter = default;
        fateRadius = 0f;
        candidateAttempt = 0;
        destination = default;
        segmentStart = default;
        progressAnchor = default;
        stateDeadline = DateTime.MinValue;
        progressDeadline = DateTime.MinValue;
        movementStartedAt = DateTime.MinValue;
        pathStarted = false;
        pathOwned = false;
        stopReason = string.Empty;
        IsActive = false;
        Status = string.Empty;
    }

    private void TickPathfinding(Vector3 playerPosition)
    {
        var now = DateTime.Now;
        if (now >= stateDeadline)
        {
            ++candidateAttempt;
            BeginStop(StopDisposition.Retry, $"pathfinding exceeded {PathfindTimeoutSeconds:F0}s", now);
            return;
        }

        if (pathTask == null || !pathTask.IsCompleted)
            return;

        try
        {
            if (pathTask.IsCanceled)
            {
                ++candidateAttempt;
                BeginStop(StopDisposition.Retry, "scoped path request was cancelled", now);
                return;
            }

            var path = pathTask.GetAwaiter().GetResult();
            if (path.Count == 0)
            {
                ++candidateAttempt;
                BeginStop(StopDisposition.Retry, "vnav returned an empty egress path", now);
                return;
            }

            if (PathRegressesTowardFate(path, playerPosition))
            {
                ++candidateAttempt;
                BeginStop(StopDisposition.Retry, "candidate path regressed toward the filler FATE instead of escaping it", now);
                return;
            }

            VNavmesh.Path.MoveTo(path, false);
            pathStarted = true;
            segmentStart = playerPosition;
            progressAnchor = playerPosition;
            progressDeadline = now.AddSeconds(MovementProgressTimeoutSeconds);
            movementStartedAt = now;
            state = EgressState.Moving;
            Status = $"Escaping filler FATE toward {FormatPosition(destination)}.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Filler egress vnav path started with {path.Count} scoped point(s) toward {FormatPosition(destination)}.");
        }
        catch (Exception exception)
        {
            ++candidateAttempt;
            BeginStop(StopDisposition.Retry, $"pathfinding failed: {exception.Message}", now);
        }
    }

    private void TickMoving(Vector3 playerPosition, bool inCombat)
    {
        var now = DateTime.Now;
        if (IsSafe(playerPosition, inCombat))
        {
            BeginStop(StopDisposition.Safe, "outside the FATE boundary and combat cleared", now);
            return;
        }

        if (HorizontalDistanceSquared(progressAnchor, playerPosition) >= ProgressDistance * ProgressDistance)
        {
            progressAnchor = playerPosition;
            progressDeadline = now.AddSeconds(MovementProgressTimeoutSeconds);
        }
        else if (now >= progressDeadline)
        {
            ++candidateAttempt;
            BeginStop(StopDisposition.Retry, $"player made less than {ProgressDistance:F1}y progress for {MovementProgressTimeoutSeconds:F0}s", now);
            return;
        }

        var arrived = HorizontalDistanceSquared(playerPosition, destination) <= ArrivalDistance * ArrivalDistance;
        if (!arrived)
        {
            if ((now - movementStartedAt).TotalSeconds < 0.5d)
                return;
            if (IsOwnedVnavActive())
                return;
        }

        var moved = MathF.Sqrt(HorizontalDistanceSquared(segmentStart, playerPosition));
        if (moved >= SuccessfulSegmentDistance)
            candidateAttempt = 0;
        else
            ++candidateAttempt;

        BeginStop(StopDisposition.Retry, arrived ? $"egress segment reached after {moved:F1}y" : $"egress path ended after {moved:F1}y", now);
    }

    private FateEgressOutcome TickStopping(Vector3 playerPosition, bool inCombat)
    {
        var now = DateTime.Now;
        if (pathOwned && IsOwnedVnavActive())
        {
            if (now >= stateDeadline)
            {
                StopOwnedVnavActivity();
                stateDeadline = now.AddSeconds(StopRetrySeconds);
                Service.PluginLog.Warning("[ZodiacBuddy/FATE] Waiting for the scoped filler-egress vnav path to stop before changing movement ownership.");
            }
            return FateEgressOutcome.Running;
        }

        pathOwned = false;
        pathStarted = false;
        DisposePathRequest();
        var disposition = stopDisposition;
        stopDisposition = StopDisposition.None;

        if (disposition == StopDisposition.Safe || IsSafe(playerPosition, inCombat))
        {
            IsActive = false;
            state = EgressState.Idle;
            Status = "Outside the filler FATE and combat is clear.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Filler egress completed safely ({stopReason}).");
            stopReason = string.Empty;
            return FateEgressOutcome.Safe;
        }

        if (disposition == StopDisposition.Failed || candidateAttempt >= MaxCandidateAttempts)
        {
            IsActive = false;
            state = EgressState.Idle;
            Status = candidateAttempt >= MaxCandidateAttempts
                ? $"Filler egress exhausted {MaxCandidateAttempts} terrain/path attempts."
                : $"Filler egress failed: {stopReason}.";
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Filler egress failed ({stopReason}); candidateAttempts={candidateAttempt}/{MaxCandidateAttempts}.");
            stopReason = string.Empty;
            return FateEgressOutcome.Failed;
        }

        state = EgressState.Idle;
        Status = $"Continuing filler egress after {stopReason}.";
        stopReason = string.Empty;
        return FateEgressOutcome.Running;
    }

    private bool StartNextPath(Vector3 playerPosition)
    {
        if (candidateAttempt >= MaxCandidateAttempts)
            return false;

        try
        {
            if (!VNavmesh.Enabled || !VNavmesh.Nav.IsReady())
            {
                Service.PluginLog.Warning("[ZodiacBuddy/FATE] Cannot begin filler egress because vnavmesh is not ready.");
                return false;
            }
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Cannot query vnavmesh readiness for filler egress: {exception.Message}");
            return false;
        }

        while (candidateAttempt < MaxCandidateAttempts)
        {
            var candidate = ResolveCandidate(playerPosition, candidateAttempt);
            if (candidate is not Vector3 resolved)
            {
                ++candidateAttempt;
                continue;
            }

            try
            {
                DisposePathRequest();
                pathCancellation = new CancellationTokenSource();
                pathTask = VNavmesh.Nav.PathfindCancelable(playerPosition, resolved, false, pathCancellation.Token);
                pathOwned = true;
                pathStarted = false;
                destination = resolved;
                state = EgressState.Pathfinding;
                stateDeadline = DateTime.Now.AddSeconds(PathfindTimeoutSeconds);
                Status = $"Pathfinding out of filler FATE toward {FormatPosition(resolved)}.";
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Scoped filler-egress pathfinding started candidate={candidateAttempt + 1}/{MaxCandidateAttempts} destination={FormatPosition(resolved)} centerDistance={HorizontalDistance(playerPosition, fateCenter):F1}y radius={fateRadius:F1}y.");
                return true;
            }
            catch (Exception exception)
            {
                DisposePathRequest();
                pathOwned = false;
                ++candidateAttempt;
                Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not start filler-egress candidate path: {exception.Message}");
            }
        }

        return false;
    }

    private Vector3? ResolveCandidate(Vector3 playerPosition, int attempt)
    {
        var outward = new Vector2(playerPosition.X - fateCenter.X, playerPosition.Z - fateCenter.Z);
        if (outward.LengthSquared() < 0.01f)
            outward = Vector2.UnitX;
        else
            outward = Vector2.Normalize(outward);

        var angle = CandidateAngles[attempt % CandidateAngles.Length];
        var ring = attempt / CandidateAngles.Length;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var direction = new Vector2(
            outward.X * cos - outward.Y * sin,
            outward.X * sin + outward.Y * cos);
        var currentRadius = HorizontalDistance(playerPosition, fateCenter);
        var targetRadius = Math.Max(fateRadius + PathTargetMargin, currentRadius + ExtensionDistance) + ring * 12f;
        var candidate = new Vector3(
            fateCenter.X + direction.X * targetRadius,
            playerPosition.Y,
            fateCenter.Z + direction.Y * targetRadius);
        var floorProbe = new Vector3(candidate.X, playerPosition.Y + 4f, candidate.Z);
        var resolved = ResolveReachableGround(floorProbe, candidate, playerPosition.Y);
        if (resolved is not Vector3 point)
            return null;

        var minimumRadius = Math.Max(fateRadius + SafeOutsideMargin, currentRadius + 6f);
        if (HorizontalDistance(point, fateCenter) < minimumRadius)
            return null;

        return point;
    }

    private static Vector3? ResolveReachableGround(Vector3 floorProbe, Vector3 reachableProbe, float playerY)
    {
        try
        {
            var floor = VNavmesh.Query.Mesh.PointOnFloor(floorProbe, false, 8f);
            if (floor is Vector3 floorPoint && MathF.Abs(floorPoint.Y - playerY) <= ProjectionVerticalTolerance)
                return floorPoint;

            var reachable = VNavmesh.Query.Mesh.NearestPointReachable(reachableProbe, 16f, ProjectionVerticalTolerance);
            if (reachable is Vector3 reachablePoint && MathF.Abs(reachablePoint.Y - playerY) <= ProjectionVerticalTolerance)
                return reachablePoint;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not project filler-egress candidate onto reachable navmesh: {exception.Message}");
        }

        return null;
    }

    private bool PathRegressesTowardFate(List<Vector3> path, Vector3 playerPosition)
    {
        var startRadius = HorizontalDistance(playerPosition, fateCenter);
        var minimumAllowedRadius = startRadius >= fateRadius
            ? fateRadius
            : Math.Max(0f, startRadius - 6f);
        return path.Any(point => HorizontalDistance(point, fateCenter) < minimumAllowedRadius);
    }

    private bool IsSafe(Vector3 playerPosition, bool inCombat)
        => !inCombat && HorizontalDistance(playerPosition, fateCenter) >= fateRadius + SafeOutsideMargin;

    private void BeginStop(StopDisposition disposition, string reason, DateTime now)
    {
        stopDisposition = disposition;
        stopReason = reason;
        if (pathOwned)
            StopOwnedVnavActivity();
        state = EgressState.Stopping;
        stateDeadline = now.AddSeconds(StopRetrySeconds);
        Status = "Stopping filler-egress vnav before the next handoff.";
    }

    private bool IsOwnedVnavActive()
    {
        if (pathTask != null && !pathTask.IsCompleted)
            return true;
        if (!pathStarted)
            return false;
        try
        {
            return VNavmesh.Path.IsRunning();
        }
        catch
        {
            return false;
        }
    }

    private void StopOwnedVnavActivity()
    {
        if (pathCancellation != null && !pathCancellation.IsCancellationRequested)
        {
            try
            {
                pathCancellation.Cancel();
            }
            catch (Exception exception)
            {
                Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not cancel scoped filler-egress path request: {exception.Message}");
            }
        }

        if (!pathStarted)
            return;

        try
        {
            if (VNavmesh.Path.IsRunning())
                VNavmesh.Path.Stop();
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not stop filler-egress vnav path: {exception.Message}");
        }
    }

    private void DisposePathRequest()
    {
        pathTask = null;
        pathCancellation?.Dispose();
        pathCancellation = null;
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
        => MathF.Sqrt(HorizontalDistanceSquared(first, second));

    private static float HorizontalDistanceSquared(Vector3 first, Vector3 second)
    {
        var x = first.X - second.X;
        var z = first.Z - second.Z;
        return x * x + z * z;
    }

    private static string FormatPosition(Vector3 position)
        => $"({position.X:F1}, {position.Y:F1}, {position.Z:F1})";

    private enum EgressState
    {
        Idle,
        Pathfinding,
        Moving,
        Stopping,
    }

    private enum StopDisposition
    {
        None,
        Retry,
        Safe,
        Failed,
    }
}
