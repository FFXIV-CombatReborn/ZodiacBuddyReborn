using Dalamud.Game.ClientState.Fates;
using System;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateOpportunityClass
{
    ClosingViable,
    Comfortable,
    LateRisk,
    LikelyMissed,
}

internal readonly record struct FateOpportunityAnalysis(
    FateOpportunityClass Class,
    bool HasProjectedCompletion,
    float EffectiveDeadlineSeconds,
    float PostArrivalWindowSeconds,
    float PredictedProgressAtArrival,
    float ProgressPerSecond,
    float MomentumConfidence,
    string Reason)
{
    internal float PressureKey
        => Class is FateOpportunityClass.LateRisk or FateOpportunityClass.LikelyMissed
            ? -PostArrivalWindowSeconds
            : EffectiveDeadlineSeconds;
}

internal static class FateOpportunityAnalyzer
{
    // I dont trust yall, so you getting what I say lol. 
    private const float ClosingWindowSeconds = 240f;
    private const float LateParticipationWindowSeconds = 45f;
    private const float LikelyMissedWindowSeconds = 10f;
    private const float LatePredictedProgressAtArrival = 82f;
    private const float LikelyMissedPredictedProgressAtArrival = 95f;

    internal static FateOpportunityAnalysis Analyze(
        in FateCandidateEvaluation candidate,
        FateTravelPlan? travelPlan,
        in FateProgressObservation observation)
    {
        var travelSeconds = travelPlan?.EstimatedSelectedSeconds ?? EstimateFallbackTravelSeconds(candidate.Distance);
        if (candidate.State == FateState.Preparing)
        {
            return new(
                FateOpportunityClass.Comfortable,
                false,
                float.MaxValue,
                float.MaxValue,
                candidate.Progress,
                0f,
                0f,
                "preparing FATE has no running completion pressure yet");
        }

        var rawDeadline = candidate.TimeRemainingSeconds >= 0
            ? (float)candidate.TimeRemainingSeconds
            : float.MaxValue;
        var hasProjection = observation.HasReliableMomentum && observation.ProjectedCompletionSeconds != float.MaxValue;
        var projectedDeadline = hasProjection ? observation.ProjectedCompletionSeconds : float.MaxValue;
        var effectiveDeadline = MathF.Min(rawDeadline, projectedDeadline);
        var postArrivalWindow = effectiveDeadline == float.MaxValue
            ? float.MaxValue
            : effectiveDeadline - travelSeconds;
        var predictedProgressAtArrival = hasProjection
            ? Math.Clamp(candidate.Progress + (observation.ProgressPerSecond * travelSeconds), 0f, 100f)
            : candidate.Progress;

        if (postArrivalWindow <= LikelyMissedWindowSeconds
            || (hasProjection && predictedProgressAtArrival >= LikelyMissedPredictedProgressAtArrival))
        {
            return new(
                FateOpportunityClass.LikelyMissed,
                hasProjection,
                effectiveDeadline,
                postArrivalWindow,
                predictedProgressAtArrival,
                observation.ProgressPerSecond,
                observation.Confidence,
                hasProjection
                    ? $"arrival is projected near completion ({predictedProgressAtArrival:F0}% at arrival, {postArrivalWindow:F0}s window)"
                    : $"travel leaves only {postArrivalWindow:F0}s before server expiration");
        }

        if (postArrivalWindow < LateParticipationWindowSeconds
            || (hasProjection && predictedProgressAtArrival >= LatePredictedProgressAtArrival))
        {
            return new(
                FateOpportunityClass.LateRisk,
                hasProjection,
                effectiveDeadline,
                postArrivalWindow,
                predictedProgressAtArrival,
                observation.ProgressPerSecond,
                observation.Confidence,
                hasProjection
                    ? $"meaningful participation is at risk ({predictedProgressAtArrival:F0}% projected at arrival, {postArrivalWindow:F0}s window)"
                    : $"travel leaves a narrow {postArrivalWindow:F0}s server-expiration window");
        }

        if (effectiveDeadline <= ClosingWindowSeconds)
        {
            return new(
                FateOpportunityClass.ClosingViable,
                hasProjection,
                effectiveDeadline,
                postArrivalWindow,
                predictedProgressAtArrival,
                observation.ProgressPerSecond,
                observation.Confidence,
                hasProjection && projectedDeadline <= rawDeadline
                    ? $"other players are projected to close the FATE in {projectedDeadline:F0}s, leaving {postArrivalWindow:F0}s after arrival"
                    : $"server expiration is closing in {rawDeadline:F0}s, leaving {postArrivalWindow:F0}s after arrival");
        }

        return new(
            FateOpportunityClass.Comfortable,
            hasProjection,
            effectiveDeadline,
            postArrivalWindow,
            predictedProgressAtArrival,
            observation.ProgressPerSecond,
            observation.Confidence,
            hasProjection
                ? $"observed momentum is not yet time-critical ({projectedDeadline:F0}s projected completion)"
                : "no reliable near-term completion pressure is currently observed");
    }

    private static float EstimateFallbackTravelSeconds(float distance)
        => distance == float.MaxValue ? float.MaxValue : distance / 10f;
}
