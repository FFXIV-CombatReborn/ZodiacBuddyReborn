namespace ZodiacBuddy.Systems.Leves;

internal enum LeveAutomationState
{
    Idle,
    TravelingToIssuer,
    Acquiring,
    TravelingToStart,
    Initiating,
    Active,
    Returning,
    CollectingReward,
    AwaitingBookCredit,
    UnavailableClosing,
    Complete,
    Unavailable,
    Failed,
}

internal readonly record struct LeveAutomationSnapshot(
    LeveAutomationState State,
    uint LeveId,
    string TargetName,
    string Status,
    int VisitedMarkers,
    ulong CombatTargetId,
    bool RsrOwned);
