using Dalamud.Game.ClientState.Fates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateCandidateRejectionReason
{
    None,
    InvalidFateId,
    Excluded,
    InactiveState,
    Backoff,
    UserBlacklisted,
    InsufficientTimeRemaining,
    ProgressAboveMaximum,
}

internal readonly record struct FateCandidateEligibilityPolicy(
    int MinimumTimeRemainingSeconds,
    int MaximumProgressPercent)
{
    internal static FateCandidateEligibilityPolicy Unrestricted => new(0, 100);
}

internal readonly record struct FateCandidateEvaluationContext(
    FateExecutionPurpose Purpose,
    Vector3? PlayerPosition,
    IReadOnlySet<ushort> Excluded,
    IReadOnlyDictionary<ushort, DateTime> BackoffUntil,
    IReadOnlySet<ushort> UserBlacklist,
    FateCandidateEligibilityPolicy EligibilityPolicy,
    DateTime Now);

internal readonly record struct FateCandidateEvaluation(
    IFate Fate,
    ushort FateId,
    FateExecutionPurpose Purpose,
    FateState State,
    int Progress,
    long TimeRemainingSeconds,
    bool HasBonus,
    bool Eligible,
    FateCandidateRejectionReason RejectionReason,
    DateTime BackoffUntil,
    int StatePriority,
    float DistanceSquared)
{
    internal float Distance => DistanceSquared == float.MaxValue ? float.MaxValue : MathF.Sqrt(DistanceSquared);
}

internal readonly record struct FateCandidateRejectionCount(
    FateCandidateRejectionReason Reason,
    int Count);

internal readonly record struct FateCandidatePoolSummary(
    int TotalCount,
    int EligibleCount,
    IReadOnlyList<FateCandidateRejectionCount> Rejections)
{
    internal string Describe()
    {
        var rejected = Rejections.Count == 0
            ? "none"
            : string.Join(", ", Rejections.Select(entry => $"{entry.Reason}={entry.Count}"));
        return $"eligible={EligibleCount}/{TotalCount}; rejected=[{rejected}]";
    }
}

internal static class FateCandidateEvaluator
{
    internal static IReadOnlyList<FateCandidateEvaluation> Evaluate(
        IEnumerable<IFate> liveFates,
        in FateCandidateEvaluationContext context)
    {
        var result = new List<FateCandidateEvaluation>();
        foreach (var fate in liveFates)
            result.Add(Evaluate(fate, context));
        return result;
    }

    internal static FateCandidatePoolSummary Summarize(IReadOnlyList<FateCandidateEvaluation> candidates)
    {
        var eligibleCount = 0;
        var rejectionCounts = new Dictionary<FateCandidateRejectionReason, int>();
        foreach (var candidate in candidates)
        {
            if (candidate.Eligible)
            {
                eligibleCount++;
                continue;
            }

            rejectionCounts.TryGetValue(candidate.RejectionReason, out var count);
            rejectionCounts[candidate.RejectionReason] = count + 1;
        }

        var rejections = new List<FateCandidateRejectionCount>();
        foreach (var reason in Enum.GetValues<FateCandidateRejectionReason>())
        {
            if (reason == FateCandidateRejectionReason.None || !rejectionCounts.TryGetValue(reason, out var count))
                continue;
            rejections.Add(new(reason, count));
        }

        return new(candidates.Count, eligibleCount, rejections);
    }

    private static FateCandidateEvaluation Evaluate(
        IFate fate,
        in FateCandidateEvaluationContext context)
    {
        var fateId = fate.FateId;
        var blockedUntil = context.BackoffUntil.TryGetValue(fateId, out var backoff) ? backoff : DateTime.MinValue;
        var rejectionReason = GetRejectionReason(
            fate,
            context.Excluded,
            context.UserBlacklist,
            context.EligibilityPolicy,
            context.Now,
            blockedUntil);
        var statePriority = fate.State == FateState.Running ? 0 : fate.State == FateState.Preparing ? 1 : int.MaxValue;
        var distanceSquared = context.PlayerPosition is not { } position || fate.Position == Vector3.Zero
            ? float.MaxValue
            : Vector3.DistanceSquared(position, fate.Position);

        return new(
            fate,
            fateId,
            context.Purpose,
            fate.State,
            (int)fate.Progress,
            fate.TimeRemaining,
            fate.HasBonus,
            rejectionReason == FateCandidateRejectionReason.None,
            rejectionReason,
            blockedUntil,
            statePriority,
            distanceSquared);
    }

    private static FateCandidateRejectionReason GetRejectionReason(
        IFate fate,
        IReadOnlySet<ushort> excluded,
        IReadOnlySet<ushort> userBlacklist,
        FateCandidateEligibilityPolicy eligibilityPolicy,
        DateTime now,
        DateTime blockedUntil)
    {
        if (fate.FateId == 0)
            return FateCandidateRejectionReason.InvalidFateId;
        if (excluded.Contains(fate.FateId))
            return FateCandidateRejectionReason.Excluded;
        if (fate.State is not (FateState.Running or FateState.Preparing))
            return FateCandidateRejectionReason.InactiveState;
        if (blockedUntil != DateTime.MinValue && now < blockedUntil)
            return FateCandidateRejectionReason.Backoff;
        if (userBlacklist.Contains(fate.FateId))
            return FateCandidateRejectionReason.UserBlacklisted;
        if (fate.State == FateState.Running
            && eligibilityPolicy.MinimumTimeRemainingSeconds > 0
            && fate.TimeRemaining >= 0
            && fate.TimeRemaining < eligibilityPolicy.MinimumTimeRemainingSeconds)
        {
            return FateCandidateRejectionReason.InsufficientTimeRemaining;
        }
        if (eligibilityPolicy.MaximumProgressPercent < 100
            && fate.Progress > eligibilityPolicy.MaximumProgressPercent)
        {
            return FateCandidateRejectionReason.ProgressAboveMaximum;
        }
        return FateCandidateRejectionReason.None;
    }
}
