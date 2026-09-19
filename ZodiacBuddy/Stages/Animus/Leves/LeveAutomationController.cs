using System;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Combat;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class LeveAutomationController
{
    internal LeveAutomationContext Context { get; } = new();
    internal RotationSolverLease RotationSolver { get; } = new();

    internal LeveAutomationState State => Context.AutomationState;
    internal string Status => Context.AutomationStatus;

    internal LeveAutomationSnapshot Snapshot
        => new(
            Context.AutomationState,
            Context.AutomationTarget.LeveId,
            Context.AutomationTarget.Name ?? string.Empty,
            Context.AutomationStatus,
            Context.VisitedMarkers.Count,
            Context.CombatTargetId,
            RotationSolver.IsOwned);

    internal bool IsTerminal
        => Context.AutomationState is LeveAutomationState.Idle
            or LeveAutomationState.Complete
            or LeveAutomationState.Unavailable
            or LeveAutomationState.Failed;

    internal void Begin(LeveObjectiveDefinition target, long runId, uint bookId, bool debugMode, bool rerollMode, bool allowUnavailableReturn, string issuerName)
    {
        Context.AutomationTarget = target;
        Context.AutomationRunId = runId;
        Context.BookId = bookId;
        Context.DebugMode = debugMode;
        Context.RerollMode = rerollMode;
        Context.AllowUnavailableReturn = allowUnavailableReturn;
        Context.CreditDeadline = DateTime.MinValue;
        Context.AutomationState = LeveAutomationState.TravelingToIssuer;
        Context.AutomationStatus = $"Traveling to {issuerName}.";
        ResetExecutionState();
    }

    internal void Reset()
    {
        Context.AutomationTarget = default;
        Context.AutomationRunId = 0;
        Context.BookId = 0;
        Context.DebugMode = false;
        Context.RerollMode = false;
        Context.AllowUnavailableReturn = false;
        Context.CreditDeadline = DateTime.MinValue;
        Context.AutomationState = LeveAutomationState.Idle;
        Context.AutomationStatus = "Idle";
        ResetExecutionState();
    }

    internal void BeginActive(string profileName)
    {
        Context.AutomationState = LeveAutomationState.Active;
        Context.AutomationStatus = $"Leve active; running {profileName} profile.";
        Context.VisitedMarkers.Clear();
        Context.NoObjectiveSince = DateTime.Now;
        Context.NextMarkerSweepAt = DateTime.MinValue;
        Context.CombatTargetId = 0;
        Context.FieldNavigationDestination = null;
        Context.EscortActorId = 0;
        Context.EscortGoal = null;
        Context.LastBeckonAt = DateTime.MinValue;
        Context.EscortFinishReachedAt = DateTime.MinValue;
        Context.EscortLeadNavigationActive = false;
        Context.LastParchmentReadAt = DateTime.MinValue;
        Context.LastEventItemUseAt = DateTime.MinValue;
        Context.TimedCullWaiting = false;
        Context.TimedCullTodoSnapshot = string.Empty;
    }


    internal string Complete()
    {
        Context.AutomationRunId = 0;
        Context.AutomationState = LeveAutomationState.Complete;
        Context.AutomationStatus = Context.DebugMode
            ? $"LeveId={Context.AutomationTarget.LeveId} completed and reward collected."
            : Context.RerollMode
                ? $"Leve reroll completed with {Context.AutomationTarget.Name}."
                : $"Book credit confirmed for {Context.AutomationTarget.Name}.";
        return Context.AutomationStatus;
    }

    internal void Fail(string message)
    {
        Context.AutomationRunId = 0;
        Context.AutomationState = LeveAutomationState.Failed;
        Context.AutomationStatus = message;
    }

    private void ResetExecutionState()
    {
        Context.VisitedMarkers.Clear();
        Context.NoObjectiveSince = DateTime.MinValue;
        Context.NextMarkerSweepAt = DateTime.MinValue;
        Context.CombatTargetId = 0;
        Context.FieldNavigationDestination = null;
        Context.AcquireCompletedAt = DateTime.MinValue;
        Context.InitiateRequestedAt = DateTime.MinValue;
        Context.RewardCompletedAt = DateTime.MinValue;
        Context.EscortActorId = 0;
        Context.StartMarker = Vector3.Zero;
        Context.EscortGoal = null;
        Context.LastBeckonAt = DateTime.MinValue;
        Context.EscortFinishReachedAt = DateTime.MinValue;
        Context.EscortMotionActorId = 0;
        Context.EscortMotionPosition = Vector3.Zero;
        Context.EscortMotionSampleAt = DateTime.MinValue;
        Context.EscortLastMovedAt = DateTime.MinValue;
        Context.EscortMovementLogged = false;
        Context.EscortLeadNavigationActive = false;
        Context.StartAwaitingDismount = false;
        Context.StartDismountRequestedAt = DateTime.MinValue;
        Context.StartLandingRecoveryAttempts = 0;
        Context.IssuerDismountRequestedAt = DateTime.MinValue;
        Context.IssuerLandingRecoveryAttempts = 0;
        Context.LastParchmentReadAt = DateTime.MinValue;
        Context.LastEventItemUseAt = DateTime.MinValue;
        Context.LurePrimeObjectId = 0;
        Context.LurePrimeInRangeAt = DateTime.MinValue;
        Context.TimedCullWaiting = false;
        Context.TimedCullTodoSnapshot = string.Empty;
    }
}
