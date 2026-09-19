using Dalamud.Game.Text.SeStringHandling.Payloads;
using System;
using System.Numerics;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateExecutionPurpose
{
    None,
    RequiredObjective,
    DebugTarget,
    Filler,
    GeneralGrinder,
    DebugFiller,
}

internal enum FateExecutionResultKind
{
    None,
    Completed,
    EndedOrFailed,
    Unavailable,
    UnsupportedMechanic,
    RetryLater,
    Preempted,
    Cancelled,
    ExecutionFailure,
}

internal readonly record struct FateExecutionRequest(
    ushort FateId,
    string Name,
    string ZoneName,
    uint TerritoryTypeId,
    MapLinkPayload? StagingMapLink,
    FateExecutionPurpose Purpose,
    bool ProbeOnlyWhenAbsent,
    FateTravelPlan? TravelPlan)
{
    internal bool IsFiller => Purpose is FateExecutionPurpose.Filler or FateExecutionPurpose.GeneralGrinder or FateExecutionPurpose.DebugFiller;
}

internal readonly record struct FateExecutionResult(
    FateExecutionResultKind Kind,
    ushort FateId,
    string Status,
    DateTime FinishedAt)
{
    internal static FateExecutionResult None => new(FateExecutionResultKind.None, 0, string.Empty, DateTime.MinValue);
}

internal readonly record struct FateRuntimeDiagnostic(
    ushort FateId,
    string Name,
    string Description,
    string Objective,
    string State,
    int Progress,
    int HandInCount,
    int StartTimeEpoch,
    int DurationSeconds,
    long TimeRemainingSeconds,
    bool HasBonus,
    int Level,
    int MaxLevel,
    uint IconId,
    uint MapIconId,
    uint TerritoryTypeId,
    Vector3 Position,
    float Radius,
    float Distance,
    bool IsCurrentFate,
    bool IsRequiredTarget,
    bool IsKnownPrerequisite,
    bool HasKnownSpecialHandling,
    int TaggedEnemies,
    int TaggedFriendlies,
    int TaggedEventObjects,
    int TaggedOtherObjects,
    string TaggedActorSummary,
    byte RuleRaw,
    string RuleName,
    ushort FateRuleEx,
    uint MotivationNpcId,
    string MotivationNpcSummary,
    uint ObjectiveNpcId,
    string ObjectiveNpcSummary,
    uint SheetEventItemId,
    int SheetEventItemCount,
    uint EventItemId,
    int EventItemCount,
    uint RequiredEventItemId,
    int RequiredEventItemCount,
    uint TurnInEventItemId,
    int TurnInEventItemCount,
    int ObjectiveMarkerCount,
    string ObjectiveMarkerSummary);

internal enum FateGrindingMode
{
    None,
    WaitForSelectedTarget,
    General,
}

internal readonly record struct FateGrindingSnapshot(
    FateGrindingMode Mode,
    bool Active,
    ushort DesiredFateId,
    ushort ActiveFillerFateId,
    string Status,
    int FillerAttempts,
    FateExecutionResult LastResult);
