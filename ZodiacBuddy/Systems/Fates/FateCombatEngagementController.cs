using System;
using System.Numerics;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateCombatEngagementPhase
{
    None,
    MacroApproach,
    TacticalApproach,
    Settling,
    Engaged,
    LineOfSightRecovery,
    ElevationRecovery,
    ObstacleMapRecovery,
    FallbackApproach,
}

internal readonly record struct FateCombatEngagementDecision(
    FateCombatEngagementPhase PreviousPhase,
    FateCombatEngagementPhase Phase,
    bool AllowRotation,
    bool PhaseChanged);

internal sealed class FateCombatEngagementController
{
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan LineOfSightBlockWindow = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan ElevationMismatchWindow = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan EngagementPokeRetryWindow = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan MeleeCaptureWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ObstacleMapStallWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ObstacleMapProbeWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ObstacleMapProbeCooldown = TimeSpan.FromSeconds(8);
    private const float ObstacleMapStallProgressDistance = 0.8f;
    private const float ObstacleMapProbeProofTravelDistance = 1.5f;

    private ulong targetId;
    private FateCombatEngagementPhase phase;
    private DateTime settledSince = DateTime.MinValue;
    private DateTime lineOfSightBlockedSince = DateTime.MinValue;
    private DateTime elevationMismatchSince = DateTime.MinValue;
    private DateTime lastEngagementPokeAt = DateTime.MinValue;
    private DateTime meleeCaptureStartedAt = DateTime.MinValue;
    private bool engagementPokeSucceeded;
    private bool meleeCaptureComplete;
    private bool hasEstablishedEngagement;
    private DateTime obstacleMapStallStartedAt = DateTime.MinValue;
    private float obstacleMapStallReferenceDistance;
    private DateTime obstacleMapProbeStartedAt = DateTime.MinValue;
    private Vector3 obstacleMapProbeStartPosition;
    private DateTime obstacleMapProbeCooldownUntil = DateTime.MinValue;

    internal ulong TargetId => targetId;
    internal FateCombatEngagementPhase Phase => phase;

    internal bool CommitTarget(ulong currentTargetId)
    {
        if (targetId == currentTargetId)
            return false;

        targetId = currentTargetId;
        phase = FateCombatEngagementPhase.None;
        settledSince = DateTime.MinValue;
        lineOfSightBlockedSince = DateTime.MinValue;
        elevationMismatchSince = DateTime.MinValue;
        lastEngagementPokeAt = DateTime.MinValue;
        meleeCaptureStartedAt = DateTime.MinValue;
        engagementPokeSucceeded = false;
        meleeCaptureComplete = false;
        hasEstablishedEngagement = false;
        obstacleMapStallStartedAt = DateTime.MinValue;
        obstacleMapStallReferenceDistance = 0f;
        obstacleMapProbeStartedAt = DateTime.MinValue;
        obstacleMapProbeStartPosition = default;
        obstacleMapProbeCooldownUntil = DateTime.MinValue;
        return true;
    }

    internal FateCombatEngagementDecision EnterMacroApproach(ulong currentTargetId)
        => EnterApproach(currentTargetId, FateCombatEngagementPhase.MacroApproach);

    internal FateCombatEngagementDecision EnterFallbackApproach(ulong currentTargetId)
        => EnterApproach(currentTargetId, FateCombatEngagementPhase.FallbackApproach);

    internal FateCombatEngagementDecision EnterLineOfSightRecovery(ulong currentTargetId)
        => EnterApproach(currentTargetId, FateCombatEngagementPhase.LineOfSightRecovery);

    internal FateCombatEngagementDecision EnterElevationRecovery(ulong currentTargetId)
        => EnterApproach(currentTargetId, FateCombatEngagementPhase.ElevationRecovery);

    internal bool ObserveLineOfSight(ulong currentTargetId, bool hasLineOfSight, DateTime now)
    {
        CommitTarget(currentTargetId);
        if (hasLineOfSight)
        {
            lineOfSightBlockedSince = DateTime.MinValue;
            return false;
        }

        if (phase is not FateCombatEngagementPhase.TacticalApproach
            and not FateCombatEngagementPhase.Settling
            and not FateCombatEngagementPhase.Engaged
            and not FateCombatEngagementPhase.FallbackApproach)
        {
            lineOfSightBlockedSince = DateTime.MinValue;
            return false;
        }

        if (lineOfSightBlockedSince == DateTime.MinValue)
            lineOfSightBlockedSince = now;

        return now - lineOfSightBlockedSince >= LineOfSightBlockWindow;
    }

    internal bool ObserveElevation(ulong currentTargetId, bool withinRecoveryTolerance, DateTime now)
    {
        CommitTarget(currentTargetId);
        if (withinRecoveryTolerance)
        {
            elevationMismatchSince = DateTime.MinValue;
            return false;
        }

        if (phase is not FateCombatEngagementPhase.TacticalApproach
            and not FateCombatEngagementPhase.Settling
            and not FateCombatEngagementPhase.Engaged)
        {
            elevationMismatchSince = DateTime.MinValue;
            return false;
        }

        if (elevationMismatchSince == DateTime.MinValue)
            elevationMismatchSince = now;

        return now - elevationMismatchSince >= ElevationMismatchWindow;
    }

    internal bool ShouldUseMeleeCaptureRange(ulong currentTargetId, bool closeRangeClassJob, DateTime now, out bool timedOut)
    {
        CommitTarget(currentTargetId);
        timedOut = false;
        if (!closeRangeClassJob || meleeCaptureComplete || hasEstablishedEngagement)
            return false;

        if (meleeCaptureStartedAt == DateTime.MinValue)
            meleeCaptureStartedAt = now;

        if (now - meleeCaptureStartedAt < MeleeCaptureWindow)
            return true;

        meleeCaptureComplete = true;
        timedOut = true;
        return false;
    }

    internal bool TryClaimEngagementPoke(ulong currentTargetId, DateTime now)
    {
        CommitTarget(currentTargetId);
        if (engagementPokeSucceeded)
            return false;

        if (lastEngagementPokeAt != DateTime.MinValue && now - lastEngagementPokeAt < EngagementPokeRetryWindow)
            return false;

        lastEngagementPokeAt = now;
        return true;
    }

    internal bool RecordEngagementPokeResult(ulong currentTargetId, bool succeeded)
    {
        CommitTarget(currentTargetId);
        if (!succeeded || engagementPokeSucceeded)
            return false;

        var captureWasActive = !meleeCaptureComplete;
        engagementPokeSucceeded = true;
        meleeCaptureComplete = true;
        return captureWasActive;
    }

    internal bool ObserveObstacleMapMeleeStall(ulong currentTargetId, bool eligible, float horizontalDistance, DateTime now)
    {
        CommitTarget(currentTargetId);
        if (!eligible || now < obstacleMapProbeCooldownUntil)
        {
            ClearObstacleMapStallSample();
            return false;
        }

        if (obstacleMapStallStartedAt == DateTime.MinValue)
        {
            obstacleMapStallStartedAt = now;
            obstacleMapStallReferenceDistance = horizontalDistance;
            return false;
        }

        if (horizontalDistance <= obstacleMapStallReferenceDistance - ObstacleMapStallProgressDistance)
        {
            obstacleMapStallStartedAt = now;
            obstacleMapStallReferenceDistance = horizontalDistance;
            return false;
        }

        return now - obstacleMapStallStartedAt >= ObstacleMapStallWindow;
    }

    internal FateCombatEngagementDecision EnterObstacleMapRecovery(ulong currentTargetId, Vector3 playerPosition, DateTime now)
    {
        CommitTarget(currentTargetId);
        var previous = phase;
        phase = FateCombatEngagementPhase.ObstacleMapRecovery;
        settledSince = DateTime.MinValue;
        lineOfSightBlockedSince = DateTime.MinValue;
        elevationMismatchSince = DateTime.MinValue;
        obstacleMapProbeStartedAt = now;
        obstacleMapProbeStartPosition = playerPosition;
        ClearObstacleMapStallSample();
        return new(previous, phase, false, previous != phase);
    }

    internal FateObstacleMapProbeOutcome EvaluateObstacleMapRecovery(
        ulong currentTargetId,
        Vector3 playerPosition,
        float horizontalDistance,
        float successDistance,
        bool localAccessReady,
        DateTime now,
        out float playerTravel)
    {
        CommitTarget(currentTargetId);
        playerTravel = HorizontalDistance(obstacleMapProbeStartPosition, playerPosition);
        if (phase != FateCombatEngagementPhase.ObstacleMapRecovery || obstacleMapProbeStartedAt == DateTime.MinValue)
            return FateObstacleMapProbeOutcome.Inactive;

        if (localAccessReady && horizontalDistance <= successDistance)
            return playerTravel >= ObstacleMapProbeProofTravelDistance
                ? FateObstacleMapProbeOutcome.ProvenReachable
                : FateObstacleMapProbeOutcome.ReachedWithoutProof;

        if (now - obstacleMapProbeStartedAt >= ObstacleMapProbeWindow)
            return FateObstacleMapProbeOutcome.TimedOut;

        return FateObstacleMapProbeOutcome.Active;
    }

    internal void CompleteObstacleMapRecovery(ulong currentTargetId, DateTime now)
    {
        CommitTarget(currentTargetId);
        phase = FateCombatEngagementPhase.None;
        settledSince = DateTime.MinValue;
        obstacleMapProbeStartedAt = DateTime.MinValue;
        obstacleMapProbeStartPosition = default;
        obstacleMapProbeCooldownUntil = now.Add(ObstacleMapProbeCooldown);
        ClearObstacleMapStallSample();
    }

    internal FateCombatEngagementDecision EvaluateTactical(
        ulong currentTargetId,
        float horizontalDistance,
        float engageRadius,
        float recloseRadius,
        bool isNavigating,
        bool movementActive,
        DateTime now)
    {
        CommitTarget(currentTargetId);
        var previous = phase;

        if (phase == FateCombatEngagementPhase.Engaged)
        {
            if (horizontalDistance > recloseRadius)
            {
                phase = FateCombatEngagementPhase.TacticalApproach;
                settledSince = DateTime.MinValue;
                return new(previous, phase, false, previous != phase);
            }

            if (isNavigating || movementActive)
            {
                phase = FateCombatEngagementPhase.Settling;
                settledSince = DateTime.MinValue;
                return new(previous, phase, false, previous != phase);
            }

            hasEstablishedEngagement = true;
            return new(previous, phase, true, false);
        }

        if (horizontalDistance > engageRadius)
        {
            phase = FateCombatEngagementPhase.TacticalApproach;
            settledSince = DateTime.MinValue;
            return new(previous, phase, false, previous != phase);
        }

        var movementSettled = !isNavigating && !movementActive;
        if (!movementSettled)
        {
            phase = FateCombatEngagementPhase.Settling;
            settledSince = DateTime.MinValue;
            return new(previous, phase, false, previous != phase);
        }

        if (settledSince == DateTime.MinValue)
            settledSince = now;

        phase = FateCombatEngagementPhase.Settling;
        if (now - settledSince < SettleWindow)
            return new(previous, phase, false, previous != phase);

        phase = FateCombatEngagementPhase.Engaged;
        hasEstablishedEngagement = true;
        return new(previous, phase, true, previous != phase);
    }

    internal void Reset()
    {
        targetId = 0;
        phase = FateCombatEngagementPhase.None;
        settledSince = DateTime.MinValue;
        lineOfSightBlockedSince = DateTime.MinValue;
        elevationMismatchSince = DateTime.MinValue;
        lastEngagementPokeAt = DateTime.MinValue;
        meleeCaptureStartedAt = DateTime.MinValue;
        engagementPokeSucceeded = false;
        meleeCaptureComplete = false;
        hasEstablishedEngagement = false;
        obstacleMapStallStartedAt = DateTime.MinValue;
        obstacleMapStallReferenceDistance = 0f;
        obstacleMapProbeStartedAt = DateTime.MinValue;
        obstacleMapProbeStartPosition = default;
        obstacleMapProbeCooldownUntil = DateTime.MinValue;
    }

    private FateCombatEngagementDecision EnterApproach(ulong currentTargetId, FateCombatEngagementPhase requestedPhase)
    {
        CommitTarget(currentTargetId);
        var previous = phase;
        phase = requestedPhase;
        settledSince = DateTime.MinValue;
        if (previous != requestedPhase)
        {
            lineOfSightBlockedSince = DateTime.MinValue;
            elevationMismatchSince = DateTime.MinValue;
            if (previous == FateCombatEngagementPhase.ObstacleMapRecovery)
            {
                obstacleMapProbeStartedAt = DateTime.MinValue;
                obstacleMapProbeStartPosition = default;
                obstacleMapProbeCooldownUntil = DateTime.Now.Add(ObstacleMapProbeCooldown);
            }
        }
        return new(previous, phase, false, previous != phase);
    }

    private void ClearObstacleMapStallSample()
    {
        obstacleMapStallStartedAt = DateTime.MinValue;
        obstacleMapStallReferenceDistance = 0f;
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return MathF.Sqrt(x * x + z * z);
    }
}

internal enum FateObstacleMapProbeOutcome
{
    Inactive,
    Active,
    ProvenReachable,
    ReachedWithoutProof,
    TimedOut,
}
