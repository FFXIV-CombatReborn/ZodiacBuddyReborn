using System;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Combat;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class FateAutomationController
{
    internal FateAutomationContext Context { get; } = new();
    internal RotationSolverLease RotationSolver { get; } = new();
    internal BossModTacticalMovementLease TacticalMovement { get; } = new();
    internal FateTerrainRecoveryController TerrainRecovery { get; } = new();
    internal FateObstacleMapController ObstacleMaps { get; } = new();
    internal FatePreemptionEgressController PreemptionEgress { get; } = new();
    internal FateCombatTargetSelector CombatTargets { get; } = new();
    internal FateCombatEngagementController CombatEngagement { get; } = new();

    internal FateAutomationState State => Context.AutomationState;
    internal string Status => Context.AutomationStatus;
    internal FateExecutionResult FinalResult => Context.FinalResult;

    internal FateAutomationSnapshot Snapshot
        => new(
            Context.AutomationState,
            Context.Request.FateId,
            Context.Request.FateId == 0 ? string.Empty : Context.Request.Name,
            Context.AutomationStatus,
            Context.Request.Purpose,
            Context.RuntimeResult,
            Context.FinalResult,
            Context.WorkingFateId,
            Context.WorkingFateIsPrerequisite);

    internal bool IsTerminal
        => Context.AutomationState is FateAutomationState.Completed
            or FateAutomationState.Unavailable
            or FateAutomationState.RetryLater
            or FateAutomationState.Failed
            or FateAutomationState.Preempted
            or FateAutomationState.Cancelled
            or FateAutomationState.Idle;

    internal void Begin(
        FateExecutionRequest request,
        FateObjectiveDefinition? bookTarget,
        uint bookId,
        long runId,
        bool prerequisiteDone)
    {
        TerrainRecovery.ResetForTacticalRelease();
        PreemptionEgress.Reset();
        TacticalMovement.Release();
        ObstacleMaps.Reset();
        CombatTargets.Reset();
        CombatEngagement.Reset();
        Context.AutomationRunId = runId;
        Context.Request = request;
        Context.RuntimeResult = FateExecutionResult.None;
        Context.FinalResult = FateExecutionResult.None;
        Context.BookTarget = bookTarget ?? default;
        Context.HasBookTarget = bookTarget.HasValue;
        Context.BookId = bookId;
        Context.ProbeOnlyWhenAbsent = request.ProbeOnlyWhenAbsent;
        Context.ProbeReadyAt = DateTime.MinValue;
        Context.NextTeleportRetryAt = DateTime.MinValue;
        Context.TeleportCastObserved = false;
        Context.TeleportCastEndedAt = DateTime.MinValue;
        Context.TeleportAetheryteId = 0;
        Context.TeleportAttempts = 0;
        Context.TeleportStartTerritoryId = 0;
        Context.TeleportStartPosition = null;
        Context.TeleportDestinationPosition = null;
        Context.TeleportTransitionObserved = false;
        Context.TeleportScreenNotReadyObserved = false;
        Context.TeleportTerritoryChangeObserved = false;
        Context.TeleportDestinationRecoveryLogged = false;
        Context.PrerequisiteDone = prerequisiteDone;
        Context.TargetObservedRunning = false;
        Context.TargetParticipated = false;
        Context.WorkingFateId = 0;
        Context.WorkingFateIsPrerequisite = false;
        Context.WorkingFateObservedPreparing = false;
        Context.WorkingFateObservedRunning = false;
        Context.WorkingFateCompletionObserved = false;
        Context.WorkingFateMissingSince = DateTime.MinValue;
        Context.CombatTargetId = 0;
        Context.NoCombatTargetSince = DateTime.MinValue;
        Context.IncidentalAggroTargetId = 0;
        Context.InteractionTargetId = 0;
        Context.StarterObjectId = 0;
        Context.EscortAnchorId = 0;
        Context.EscortFollowRestartAt = DateTime.MinValue;
        Context.DefendAnchorId = 0;
        Context.EventObjectSafeAt = DateTime.MinValue;
        Context.CollectTurnInActive = false;
        Context.CollectTurnInSafeAt = DateTime.MinValue;
        Context.CollectTurnInStartItemCount = 0;
        Context.CollectRequestFillSlot = 0;
        Context.CollectRequestSubmitAfterFrame = 0;
        Context.StagingDestination = null;
        Context.EntryArrivalMisses = 0;
        Context.EntryArrivalGraceUntil = DateTime.MinValue;
        Context.EntryAnchorId = 0;
        Context.EntryAnchorDestination = null;
        Context.FillerStartDeadline = DateTime.MinValue;
        Context.PreemptionPending = false;
        Context.PreemptionReason = string.Empty;
        Context.PreemptionFateId = 0;
        Context.PreemptionFateCenter = default;
        Context.PreemptionFateRadius = 0f;
        Context.PreemptionUnsyncNextAttemptAt = DateTime.MinValue;
        Context.PreemptionUnsyncAttempts = 0;
        Context.FinishFillerBeforeYield = false;
        Context.DeferredTerminalStatus = string.Empty;
        Context.KnownCombatEnemyIds.Clear();
        Context.ResidualAggroOrigin = null;
        Context.ResidualLoggedTargetId = 0;
        ResetResidualDiagnostics();
        Context.NavmeshWaitRequested = false;
        Context.NavigationActive = false;
        Context.NavigationDestination = null;
        Context.NavigationIntent = FateNavigationIntent.None;
        Context.NavigationFly = false;
        Context.UGhamaroMineGroundTravelActive = false;
        Context.LineOfSightRecoveryRetryAt = DateTime.MinValue;
        Context.DismountQueued = false;
        Context.DismountDeadline = DateTime.MinValue;
        Context.LandingRecoveryAnchor = null;
        Context.LandingRecoveryAttempts = 0;
        Context.PostCombatOutcome = FatePostCombatOutcome.None;
        Context.AutomationState = FateAutomationState.Idle;
        Context.AutomationStatus = request.Purpose switch
        {
            FateExecutionPurpose.RequiredObjective => $"Starting book FATE {request.Name} ({request.FateId}).",
            FateExecutionPurpose.DebugTarget => $"Debug run starting for {request.Name} ({request.FateId}).",
            FateExecutionPurpose.Filler => $"Book filler run starting for {request.Name} ({request.FateId}).",
            FateExecutionPurpose.GeneralGrinder => $"General grinder starting {request.Name} ({request.FateId}).",
            FateExecutionPurpose.DebugFiller => $"Debug grinder starting {request.Name} ({request.FateId}).",
            _ => $"Starting FATE {request.Name} ({request.FateId}).",
        };
    }

    internal void Finish(FateAutomationState terminalState, FateExecutionResultKind resultKind, string status)
    {
        TerrainRecovery.ResetForTacticalRelease();
        PreemptionEgress.Reset();
        TacticalMovement.Release();
        ObstacleMaps.Reset();
        CombatTargets.Reset();
        CombatEngagement.Reset();
        Context.AutomationState = terminalState;
        Context.AutomationStatus = status;
        Context.FinalResult = new FateExecutionResult(resultKind, Context.Request.FateId, status, DateTime.Now);
        if (Context.RuntimeResult.Kind == FateExecutionResultKind.None)
            Context.RuntimeResult = Context.FinalResult;
        Context.AutomationRunId = 0;
    }

    internal bool Reset(bool cancelled)
    {
        TerrainRecovery.ResetForTacticalRelease();
        PreemptionEgress.Reset();
        TacticalMovement.Release();
        ObstacleMaps.Reset();
        CombatTargets.Reset();
        CombatEngagement.Reset();
        var hadRun = Context.AutomationRunId != 0 || !IsTerminal;
        Context.AutomationRunId = 0;
        Context.ProbeOnlyWhenAbsent = false;
        Context.ProbeReadyAt = DateTime.MinValue;
        Context.NextTeleportRetryAt = DateTime.MinValue;
        Context.TeleportCastObserved = false;
        Context.TeleportCastEndedAt = DateTime.MinValue;
        Context.TeleportAetheryteId = 0;
        Context.TeleportAttempts = 0;
        Context.TeleportStartTerritoryId = 0;
        Context.TeleportStartPosition = null;
        Context.TeleportDestinationPosition = null;
        Context.TeleportTransitionObserved = false;
        Context.TeleportScreenNotReadyObserved = false;
        Context.TeleportTerritoryChangeObserved = false;
        Context.TeleportDestinationRecoveryLogged = false;
        Context.NavigationActive = false;
        Context.NavigationDestination = null;
        Context.NavigationIntent = FateNavigationIntent.None;
        Context.NavigationFly = false;
        Context.UGhamaroMineGroundTravelActive = false;
        Context.LineOfSightRecoveryRetryAt = DateTime.MinValue;
        Context.DismountQueued = false;
        Context.DismountDeadline = DateTime.MinValue;
        Context.LandingRecoveryAnchor = null;
        Context.LandingRecoveryAttempts = 0;
        Context.NavmeshWaitRequested = false;
        Context.WorkingFateId = 0;
        Context.WorkingFateIsPrerequisite = false;
        Context.WorkingFateObservedPreparing = false;
        Context.WorkingFateObservedRunning = false;
        Context.WorkingFateCompletionObserved = false;
        Context.WorkingFateMissingSince = DateTime.MinValue;
        Context.CombatTargetId = 0;
        Context.NoCombatTargetSince = DateTime.MinValue;
        Context.IncidentalAggroTargetId = 0;
        Context.InteractionTargetId = 0;
        Context.StarterObjectId = 0;
        Context.EscortAnchorId = 0;
        Context.EscortFollowRestartAt = DateTime.MinValue;
        Context.DefendAnchorId = 0;
        Context.EventObjectSafeAt = DateTime.MinValue;
        Context.CollectTurnInActive = false;
        Context.CollectTurnInSafeAt = DateTime.MinValue;
        Context.CollectTurnInStartItemCount = 0;
        Context.CollectRequestFillSlot = 0;
        Context.CollectRequestSubmitAfterFrame = 0;
        Context.StagingDestination = null;
        Context.EntryArrivalMisses = 0;
        Context.EntryArrivalGraceUntil = DateTime.MinValue;
        Context.EntryAnchorId = 0;
        Context.EntryAnchorDestination = null;
        Context.FillerStartDeadline = DateTime.MinValue;
        Context.PreemptionPending = false;
        Context.PreemptionReason = string.Empty;
        Context.PreemptionFateId = 0;
        Context.PreemptionFateCenter = default;
        Context.PreemptionFateRadius = 0f;
        Context.PreemptionUnsyncNextAttemptAt = DateTime.MinValue;
        Context.PreemptionUnsyncAttempts = 0;
        Context.FinishFillerBeforeYield = false;
        Context.DeferredTerminalStatus = string.Empty;
        Context.KnownCombatEnemyIds.Clear();
        Context.ResidualAggroOrigin = null;
        Context.ResidualLoggedTargetId = 0;
        ResetResidualDiagnostics();
        Context.PostCombatOutcome = FatePostCombatOutcome.None;
        if (cancelled && hadRun && Context.AutomationState is not (FateAutomationState.Completed or FateAutomationState.Unavailable or FateAutomationState.RetryLater or FateAutomationState.Failed or FateAutomationState.Preempted))
        {
            Context.AutomationState = FateAutomationState.Cancelled;
            Context.AutomationStatus = "FATE automation cancelled.";
            Context.FinalResult = new FateExecutionResult(FateExecutionResultKind.Cancelled, Context.Request.FateId, Context.AutomationStatus, DateTime.Now);
            if (Context.RuntimeResult.Kind == FateExecutionResultKind.None)
                Context.RuntimeResult = Context.FinalResult;
        }
        return hadRun;
    }

    internal void ResetResidualDiagnostics()
    {
        Context.ResidualDiagnosticTargetId = 0;
        Context.ResidualDiagnosticFateId = 0;
        Context.ResidualDiagnosticNextAt = DateTime.MinValue;
    }
}
