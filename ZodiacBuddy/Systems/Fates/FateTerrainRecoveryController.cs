using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ZodiacBuddy.Systems.Combat;

namespace ZodiacBuddy.Systems.Fates;

internal sealed class FateTerrainRecoveryController
{
    private const double StallSeconds = 2.0d;
    private const double PauseSettleSeconds = 1d;
    private const double RecoveryTimeoutSeconds = 4d;
    private const double RecoveryCooldownSeconds = 4d;
    private const double StopRetrySeconds = 1d;
    private const float MinimumIntentDistance = 2f;
    private const float StallProgressDistance = 0.8f;
    private const float DestinationShiftDistance = 3f;
    private const float RecoveryTravelDistance = 5f;
    private const float RecoveryArrivalDistance = 1.5f;
    private const int MaxConsecutiveAttempts = 3;

    private RecoveryState state;
    private ulong targetId;
    private bool hasStallSample;
    private Vector3 stallSamplePosition;
    private Vector3 stallSampleDestination;
    private string stallIntentSource = string.Empty;
    private DateTime stallSampleStartedAt;
    private DateTime cooldownUntil;
    private int consecutiveAttempts;
    private Vector3 recoveryStartPosition;
    private Vector3 recoveryDestination;
    private DateTime recoveryStartedAt;
    private DateTime stateDeadline;
    private bool bossModPaused;
    private bool vnavOwned;
    private bool recoveryPathStarted;
    private CancellationTokenSource? recoveryPathCancellation;
    private Task<List<Vector3>>? recoveryPathTask;
    private string recoveryStopReason = string.Empty;

    internal bool IsRecovering => state != RecoveryState.Monitoring;
    internal string Status { get; private set; } = string.Empty;

    internal void Tick(BossModTacticalMovementLease tacticalMovement, ulong currentTargetId, Vector3 playerPosition)
    {
        if (!tacticalMovement.IsOwned)
        {
            ResetForTacticalRelease();
            targetId = currentTargetId;
            return;
        }

        if (currentTargetId == 0)
        {
            targetId = 0;
            consecutiveAttempts = 0;
            cooldownUntil = DateTime.MinValue;
            ClearStallSample();

            if (state == RecoveryState.WaitingForBossModPause)
                ResumeBossModAfterAbortedRecovery(DateTime.Now);
            else if (state is RecoveryState.Pathfinding or RecoveryState.Recovering)
                BeginVnavStop("combat target cleared", DateTime.Now);

            return;
        }

        if (targetId != currentTargetId)
        {
            targetId = currentTargetId;
            consecutiveAttempts = 0;
            cooldownUntil = DateTime.MinValue;
            ClearStallSample();

            if (state == RecoveryState.WaitingForBossModPause)
            {
                ResumeBossModAfterAbortedRecovery(DateTime.Now);
                return;
            }

            if (state is RecoveryState.Pathfinding or RecoveryState.Recovering)
            {
                BeginVnavStop("combat target changed", DateTime.Now);
                return;
            }
        }

        switch (state)
        {
            case RecoveryState.Monitoring:
                TickMonitoring(playerPosition);
                break;
            case RecoveryState.WaitingForBossModPause:
                TickWaitingForBossModPause(playerPosition);
                break;
            case RecoveryState.Pathfinding:
                TickPathfinding(playerPosition);
                break;
            case RecoveryState.Recovering:
                TickRecovering(playerPosition);
                break;
            case RecoveryState.StoppingVnav:
                TickStoppingVnav(tacticalMovement, playerPosition);
                break;
        }
    }

    internal void ResetForTacticalRelease()
    {
        if (vnavOwned)
            StopOwnedVnavActivity();

        state = RecoveryState.Monitoring;
        bossModPaused = false;
        vnavOwned = false;
        recoveryPathStarted = false;
        DisposePathRequest();
        Status = string.Empty;
        recoveryStopReason = string.Empty;
        targetId = 0;
        cooldownUntil = DateTime.MinValue;
        consecutiveAttempts = 0;
        ClearStallSample();
    }

    private void TickMonitoring(Vector3 playerPosition)
    {
        var now = DateTime.Now;
        if (now < cooldownUntil)
        {
            ClearStallSample();
            return;
        }

        if (!BossModIPC.TryGetNavigationState(out var navigationState))
        {
            ClearStallSample();
            return;
        }

        var casting = Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Casting87];
        if (!navigationState.MovementActive && casting)
        {
            ClearStallSample();
            return;
        }

        if (!navigationState.IsNavigating
            || navigationState.Destination is not Vector3 destination
            || HorizontalDistanceSquared(playerPosition, destination) < MinimumIntentDistance * MinimumIntentDistance)
        {
            ClearStallSample();
            return;
        }

        var intentSource = navigationState.MovementActive ? "BossMod active movement" : "BossMod navigation intent without forced movement";

        if (!hasStallSample)
        {
            SetStallSample(playerPosition, destination, intentSource, now);
            return;
        }

        if (HorizontalDistanceSquared(stallSampleDestination, destination) >= DestinationShiftDistance * DestinationShiftDistance)
        {
            SetStallSample(playerPosition, destination, intentSource, now);
            return;
        }

        if (HorizontalDistanceSquared(stallSamplePosition, playerPosition) >= StallProgressDistance * StallProgressDistance)
        {
            SetStallSample(playerPosition, destination, intentSource, now);
            if (consecutiveAttempts > 0)
                consecutiveAttempts = 0;
            return;
        }

        stallIntentSource = intentSource;
        if ((now - stallSampleStartedAt).TotalSeconds < StallSeconds)
            return;

        if (consecutiveAttempts >= MaxConsecutiveAttempts)
        {
            consecutiveAttempts = 0;
            cooldownUntil = now.AddSeconds(RecoveryCooldownSeconds * 2d);
            SetStallSample(playerPosition, destination, intentSource, now);
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Terrain recovery reached {MaxConsecutiveAttempts} attempts on target {targetId}; cooling down.");
            return;
        }

        BeginBossModPause(playerPosition, destination, now);
    }

    private void BeginBossModPause(Vector3 playerPosition, Vector3 destination, DateTime now)
    {
        if (!TrySetBossModPaused(true))
        {
            cooldownUntil = now.AddSeconds(RecoveryCooldownSeconds);
            ClearStallSample();
            Service.PluginLog.Warning("[ZodiacBuddy/FATE] Terrain recovery could not pause BossMod movement.");
            return;
        }

        bossModPaused = true;
        recoveryStartPosition = playerPosition;
        recoveryDestination = destination;
        state = RecoveryState.WaitingForBossModPause;
        stateDeadline = now.AddSeconds(PauseSettleSeconds);
        Status = $"Tactical progress stalled; pausing BossMod before vnav recovery toward {FormatPosition(destination)}.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Terrain recovery starting for target {targetId}; source={stallIntentSource}.");
    }

    private void TickWaitingForBossModPause(Vector3 playerPosition)
    {
        var now = DateTime.Now;
        if (BossModIPC.TryGetNavigationIntent(out _, out var movementActive) && !movementActive)
        {
            StartVnavRecoveryPathfind(playerPosition, now);
            return;
        }

        if (now < stateDeadline)
            return;

        Service.PluginLog.Warning("[ZodiacBuddy/FATE] BossMod movement did not pause; terrain recovery abandoned.");
        ResumeBossModAfterAbortedRecovery(now);
    }

    private void StartVnavRecoveryPathfind(Vector3 playerPosition, DateTime now)
    {
        try
        {
            if (!VNavmesh.Nav.IsReady())
            {
                Service.PluginLog.Warning("[ZodiacBuddy/FATE] vnavmesh is not ready; terrain recovery abandoned.");
                ResumeBossModAfterAbortedRecovery(now);
                return;
            }

            var resolvedDestination = ResolveVnavDestination(playerPosition, recoveryDestination);
            if (resolvedDestination is not Vector3 destination)
            {
                Service.PluginLog.Warning("[ZodiacBuddy/FATE] No reachable terrain-recovery destination; recovery abandoned.");
                ResumeBossModAfterAbortedRecovery(now);
                return;
            }

            recoveryDestination = destination;
            DisposePathRequest();
            recoveryPathCancellation = new CancellationTokenSource();
            recoveryPathTask = VNavmesh.Nav.PathfindCancelable(playerPosition, recoveryDestination, false, recoveryPathCancellation.Token);
            vnavOwned = true;
            recoveryPathStarted = false;
            recoveryStartPosition = playerPosition;
            recoveryStartedAt = now;
            stateDeadline = now.AddSeconds(RecoveryTimeoutSeconds);
            state = RecoveryState.Pathfinding;
            Status = $"vnav terrain recovery pathfinding toward BossMod destination {FormatPosition(recoveryDestination)}.";
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Terrain-recovery pathfinding failed to start: {exception.Message}");
            ResumeBossModAfterAbortedRecovery(now);
        }
    }

    private void TickPathfinding(Vector3 playerPosition)
    {
        var now = DateTime.Now;
        if (now >= stateDeadline)
        {
            BeginVnavStop($"{RecoveryTimeoutSeconds:F1}s recovery timeout reached while pathfinding", now);
            return;
        }

        if (recoveryPathTask == null || !recoveryPathTask.IsCompleted)
            return;

        try
        {
            if (recoveryPathTask.IsCanceled)
            {
                BeginVnavStop("scoped vnav pathfinding was cancelled", now);
                return;
            }

            var path = recoveryPathTask.GetAwaiter().GetResult();
            if (path.Count == 0)
            {
                BeginVnavStop("vnav returned an empty terrain-recovery path", now);
                return;
            }

            VNavmesh.Path.MoveTo(path, false);
            recoveryPathStarted = true;
            recoveryStartPosition = playerPosition;
            recoveryStartedAt = now;
            state = RecoveryState.Recovering;
            Status = $"vnav terrain recovery active toward BossMod destination {FormatPosition(recoveryDestination)}.";
        }
        catch (Exception exception)
        {
            BeginVnavStop($"vnav pathfinding failed: {exception.Message}", now);
        }
    }

    private void TickRecovering(Vector3 playerPosition)
    {
        var now = DateTime.Now;
        var movedSquared = HorizontalDistanceSquared(recoveryStartPosition, playerPosition);
        if (movedSquared >= RecoveryTravelDistance * RecoveryTravelDistance)
        {
            BeginVnavStop($"player moved {MathF.Sqrt(movedSquared):F1}y", now);
            return;
        }

        if (HorizontalDistanceSquared(playerPosition, recoveryDestination) <= RecoveryArrivalDistance * RecoveryArrivalDistance)
        {
            BeginVnavStop("captured BossMod destination reached", now);
            return;
        }

        if (now >= stateDeadline)
        {
            BeginVnavStop($"{RecoveryTimeoutSeconds:F1}s recovery timeout reached", now);
            return;
        }

        if ((now - recoveryStartedAt).TotalSeconds >= 0.5d && !IsOwnedVnavActive())
            BeginVnavStop("vnav recovery path ended", now);
    }

    private void BeginVnavStop(string reason, DateTime now)
    {
        recoveryStopReason = reason;
        if (vnavOwned)
            StopOwnedVnavActivity();
        state = RecoveryState.StoppingVnav;
        stateDeadline = now.AddSeconds(StopRetrySeconds);
        Status = "Stopping vnav terrain recovery before returning movement to BossMod.";
    }

    private void TickStoppingVnav(BossModTacticalMovementLease tacticalMovement, Vector3 playerPosition)
    {
        var now = DateTime.Now;
        if (vnavOwned && IsOwnedVnavActive())
        {
            if (now >= stateDeadline)
            {
                StopOwnedVnavActivity();
                stateDeadline = now.AddSeconds(StopRetrySeconds);
            }
            return;
        }

        vnavOwned = false;
        recoveryPathStarted = false;
        DisposePathRequest();
        var moved = MathF.Sqrt(HorizontalDistanceSquared(recoveryStartPosition, playerPosition));
        var resumedBossMod = TrySetBossModPaused(false);
        bossModPaused = false;
        if (!resumedBossMod)
        {
            Service.PluginLog.Warning("[ZodiacBuddy/FATE] BossMod movement did not resume; tactical lease released.");
            tacticalMovement.Release();
        }

        ++consecutiveAttempts;
        cooldownUntil = now.AddSeconds(RecoveryCooldownSeconds);
        state = RecoveryState.Monitoring;
        Status = string.Empty;
        ClearStallSample();
        var handoff = resumedBossMod ? "BossMod tactical movement resumed" : "BossMod tactical lease released";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Terrain recovery finished: {recoveryStopReason}; moved={moved:F1}y, {handoff}, attempt={consecutiveAttempts}/{MaxConsecutiveAttempts}.");
        recoveryStopReason = string.Empty;
    }

    private void ResumeBossModAfterAbortedRecovery(DateTime now)
    {
        if (vnavOwned)
            StopOwnedVnavActivity();
        vnavOwned = false;
        recoveryPathStarted = false;
        DisposePathRequest();
        if (bossModPaused && !TrySetBossModPaused(false))
            Service.PluginLog.Warning("[ZodiacBuddy/FATE] BossMod movement did not resume after aborted terrain recovery.");

        bossModPaused = false;
        state = RecoveryState.Monitoring;
        cooldownUntil = now.AddSeconds(RecoveryCooldownSeconds);
        Status = string.Empty;
        ClearStallSample();
    }

    private static bool TrySetBossModPaused(bool paused)
        => BossModIPC.TryPauseMovement(paused)
            || BossModIPC.TrySetConfiguration("ForbidMovement", paused.ToString());

    private static Vector3? ResolveVnavDestination(Vector3 playerPosition, Vector3 bossModDestination)
    {
        var probe = new Vector3(bossModDestination.X, playerPosition.Y, bossModDestination.Z);
        var floorProbe = new Vector3(probe.X, playerPosition.Y + 3f, probe.Z);
        var floor = VNavmesh.Query.Mesh.PointOnFloor(floorProbe, false, 4f);
        if (floor is Vector3 floorPoint
            && MathF.Abs(floorPoint.Y - playerPosition.Y) <= 8f
            && HorizontalDistanceSquared(floorPoint, probe) <= 16f)
            return floorPoint;

        var reachable = VNavmesh.Query.Mesh.NearestPointReachable(probe, 6f, 8f);
        if (reachable is Vector3 reachablePoint
            && MathF.Abs(reachablePoint.Y - playerPosition.Y) <= 8f
            && HorizontalDistanceSquared(reachablePoint, probe) <= 36f)
            return reachablePoint;

        return null;
    }

    private bool IsOwnedVnavActive()
    {
        if (recoveryPathTask != null && !recoveryPathTask.IsCompleted)
            return true;

        if (!recoveryPathStarted)
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
        if (recoveryPathCancellation != null && !recoveryPathCancellation.IsCancellationRequested)
        {
            try
            {
                recoveryPathCancellation.Cancel();
            }
            catch (Exception exception)
            {
                Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Terrain-recovery path cancellation failed: {exception.Message}");
            }
        }

        if (!recoveryPathStarted)
            return;

        try
        {
            if (VNavmesh.Path.IsRunning())
                VNavmesh.Path.Stop();
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Terrain-recovery path stop failed: {exception.Message}");
        }
    }

    private void DisposePathRequest()
    {
        recoveryPathTask = null;
        recoveryPathCancellation?.Dispose();
        recoveryPathCancellation = null;
    }

    private void SetStallSample(Vector3 playerPosition, Vector3 destination, string intentSource, DateTime now)
    {
        hasStallSample = true;
        stallSamplePosition = playerPosition;
        stallSampleDestination = destination;
        stallIntentSource = intentSource;
        stallSampleStartedAt = now;
    }

    private void ClearStallSample()
    {
        hasStallSample = false;
        stallSamplePosition = default;
        stallSampleDestination = default;
        stallIntentSource = string.Empty;
        stallSampleStartedAt = DateTime.MinValue;
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return x * x + z * z;
    }

    private static string FormatPosition(Vector3 position)
        => $"<{position.X:F1}, {position.Y:F1}, {position.Z:F1}>";

    private enum RecoveryState
    {
        Monitoring,
        WaitingForBossModPause,
        Pathfinding,
        Recovering,
        StoppingVnav,
    }
}
