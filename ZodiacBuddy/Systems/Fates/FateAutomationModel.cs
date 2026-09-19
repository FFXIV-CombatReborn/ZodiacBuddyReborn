namespace ZodiacBuddy.Systems.Fates;

internal enum FateAutomationState
{
    Idle,
    AwaitingZone,
    Probing,
    AwaitingNavmesh,
    Resolving,
    Traveling,
    StartingNpc,
    Fighting,
    ClearingAggro,
    PreemptionUnsync,
    PreemptionEgress,
    AwaitingExternalConfirmation,
    Completed,
    Unavailable,
    RetryLater,
    Failed,
    Preempted,
    Cancelled,
}

internal readonly record struct FateAutomationSnapshot(
    FateAutomationState State,
    ushort TargetFateId,
    string TargetName,
    string Status,
    FateExecutionPurpose Purpose,
    FateExecutionResult RuntimeResult,
    FateExecutionResult FinalResult,
    ushort WorkingFateId,
    bool WorkingPrerequisite);

internal enum FateNavigationIntent
{
    None,
    Staging,
    FateEntry,
    FateEntryAnchor,
    StarterNpc,
    CombatTarget,
    PullCandidate,
    EventObject,
    CollectTurnIn,
    ObjectiveAggro,
    EscortAnchor,
    DefendAnchor,
    IncidentalAggro,
    ResidualAggro,
    ReacquireCenter,
    LandingRecovery,
}

internal enum FatePostCombatOutcome
{
    None,
    PrerequisiteComplete,
    PrerequisiteRetry,
    TargetComplete,
    TargetFailed,
    Preempted,
}
