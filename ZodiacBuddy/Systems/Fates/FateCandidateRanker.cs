using System;
using System.Collections.Generic;
using System.Linq;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateRankCriterion
{
    None,
    Opportunity,
    State,
    TravelBand,
    BonusTwistOpportunity,
    Bonus,
    OpportunityPressure,
    ExactTravel,
    FateId,
}

internal readonly record struct FateGeneralRankInput(
    FateCandidateEvaluation Candidate,
    FateTravelPlan? TravelPlan,
    FateProgressObservation Observation,
    FateOpportunityAnalysis Opportunity);

internal readonly record struct FateRankedCandidate(
    FateCandidateEvaluation Candidate,
    FateTravelPlan? TravelPlan,
    FateProgressObservation Observation,
    FateOpportunityAnalysis Opportunity,
    int TravelBand,
    bool BonusTwistOpportunity,
    bool BonusPreferred)
{
    internal float TravelSeconds
        => TravelPlan?.EstimatedSelectedSeconds
            ?? (Candidate.Distance == float.MaxValue ? float.MaxValue : Candidate.Distance / 10f);
}

internal static class FateCandidateRanker
{
    private const float TravelMaterialityBandSeconds = 20f;

    internal static bool TrySelectLegacy(
        IReadOnlyList<FateCandidateEvaluation> candidates,
        out FateCandidateEvaluation selected)
    {
        FateCandidateEvaluation? best = null;
        foreach (var candidate in candidates)
        {
            if (!candidate.Eligible)
                continue;

            if (best is not { } current
                || CompareLegacy(candidate, current) < 0)
            {
                best = candidate;
            }
        }

        if (best is { } match)
        {
            selected = match;
            return true;
        }

        selected = default;
        return false;
    }

    internal static bool TrySelectGeneral(
        IReadOnlyList<FateGeneralRankInput> inputs,
        bool preferBonusFates,
        bool playerHasTwistOfFate,
        out FateRankedCandidate selected,
        out FateRankedCandidate runnerUp,
        out FateRankCriterion decisiveCriterion,
        out IReadOnlyList<FateRankedCandidate> rankedCandidates)
    {
        var eligible = inputs.Where(input => input.Candidate.Eligible).ToArray();
        if (eligible.Length == 0)
        {
            selected = default;
            runnerUp = default;
            decisiveCriterion = FateRankCriterion.None;
            rankedCandidates = Array.Empty<FateRankedCandidate>();
            return false;
        }

        var ranked = new List<FateRankedCandidate>(eligible.Length);
        foreach (var input in eligible)
        {
            var travelSeconds = GetTravelSeconds(input);
            var bestComparableTravel = eligible
                .Where(other => other.Opportunity.Class == input.Opportunity.Class
                    && other.Candidate.StatePriority == input.Candidate.StatePriority)
                .Select(GetTravelSeconds)
                .DefaultIfEmpty(float.MaxValue)
                .Min();
            var travelBand = GetTravelBand(travelSeconds, bestComparableTravel);
            var bonusPreferred = preferBonusFates && input.Candidate.HasBonus;
            var bonusTwistOpportunity = bonusPreferred && !playerHasTwistOfFate;
            ranked.Add(new(
                input.Candidate,
                input.TravelPlan,
                input.Observation,
                input.Opportunity,
                travelBand,
                bonusTwistOpportunity,
                bonusPreferred));
        }

        ranked.Sort((left, right) => CompareGeneral(left, right, out _));
        rankedCandidates = ranked;
        selected = ranked[0];
        if (ranked.Count > 1)
        {
            runnerUp = ranked[1];
            _ = CompareGeneral(selected, runnerUp, out decisiveCriterion);
        }
        else
        {
            runnerUp = default;
            decisiveCriterion = FateRankCriterion.None;
        }
        return true;
    }

    internal static int CompareGeneral(
        in FateRankedCandidate left,
        in FateRankedCandidate right,
        out FateRankCriterion decisiveCriterion)
    {
        var comparison = left.Candidate.StatePriority.CompareTo(right.Candidate.StatePriority);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.State, out decisiveCriterion);

        comparison = left.Opportunity.Class.CompareTo(right.Opportunity.Class);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.Opportunity, out decisiveCriterion);

        comparison = left.TravelBand.CompareTo(right.TravelBand);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.TravelBand, out decisiveCriterion);

        comparison = right.BonusTwistOpportunity.CompareTo(left.BonusTwistOpportunity);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.BonusTwistOpportunity, out decisiveCriterion);

        comparison = right.BonusPreferred.CompareTo(left.BonusPreferred);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.Bonus, out decisiveCriterion);

        comparison = CompareFloat(left.Opportunity.PressureKey, right.Opportunity.PressureKey);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.OpportunityPressure, out decisiveCriterion);

        comparison = CompareFloat(left.TravelSeconds, right.TravelSeconds);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.ExactTravel, out decisiveCriterion);

        comparison = left.Candidate.FateId.CompareTo(right.Candidate.FateId);
        if (comparison != 0)
            return Decide(comparison, FateRankCriterion.FateId, out decisiveCriterion);

        decisiveCriterion = FateRankCriterion.None;
        return 0;
    }

    private static int CompareLegacy(in FateCandidateEvaluation left, in FateCandidateEvaluation right)
    {
        var comparison = left.StatePriority.CompareTo(right.StatePriority);
        if (comparison != 0)
            return comparison;
        comparison = CompareFloat(left.DistanceSquared, right.DistanceSquared);
        if (comparison != 0)
            return comparison;
        return left.FateId.CompareTo(right.FateId);
    }

    private static float GetTravelSeconds(FateGeneralRankInput input)
        => input.TravelPlan?.EstimatedSelectedSeconds
            ?? (input.Candidate.Distance == float.MaxValue ? float.MaxValue : input.Candidate.Distance / 10f);

    private static int GetTravelBand(float travelSeconds, float bestComparableTravel)
    {
        if (travelSeconds == float.MaxValue || bestComparableTravel == float.MaxValue)
            return int.MaxValue;
        return Math.Max(0, (int)MathF.Floor((travelSeconds - bestComparableTravel) / TravelMaterialityBandSeconds));
    }

    private static int CompareFloat(float left, float right)
    {
        if (left == right)
            return 0;
        if (float.IsNaN(left))
            return 1;
        if (float.IsNaN(right))
            return -1;
        return left < right ? -1 : 1;
    }

    private static int Decide(int comparison, FateRankCriterion criterion, out FateRankCriterion decisiveCriterion)
    {
        decisiveCriterion = criterion;
        return comparison;
    }
}
