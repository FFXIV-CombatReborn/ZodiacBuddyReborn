namespace ZodiacBuddy.Stages.Animus;

internal enum EnemyAutomationState
{
    Idle,
    AwaitingAtmaPathing,
    Active,
}

internal enum EnemyNavigationDisplayState
{
    None,
    NavmeshNotReady,
    GeneratingPath,
    Pathing,
}

internal readonly record struct EnemyAutomationSnapshot(
    EnemyAutomationState State,
    string? TargetName,
    ulong TargetId,
    byte? BookProgress,
    int PendingCredits,
    bool WaitingForBookCredit,
    bool ClearingCombat,
    EnemyNavigationDisplayState NavigationState);
