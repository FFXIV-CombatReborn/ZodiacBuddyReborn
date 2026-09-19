using Dalamud.Game.ClientState.Fates;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZodiacBuddy.Systems.Fates;

internal readonly record struct FateProgressObservation(
    ushort FateId,
    int CurrentProgress,
    bool HasReliableMomentum,
    float ProgressPerSecond,
    float ProjectedCompletionSeconds,
    float LastProgressAgeSeconds,
    float ObservationSpanSeconds,
    int ProgressDelta,
    float Confidence)
{
    internal static FateProgressObservation Unknown(ushort fateId, int progress)
        => new(fateId, progress, false, 0f, float.MaxValue, float.MaxValue, 0f, 0, 0f);
}

internal sealed class FateObservationTracker
{
    private const float MinimumSampleIntervalSeconds = 2f;
    private const float MomentumWindowSeconds = 45f;
    private const float MomentumStaleAfterSeconds = 30f;
    private const float MinimumMomentumSpanSeconds = 6f;
    private const int MinimumMomentumProgressDelta = 3;
    private const int MinimumMomentumProgressChanges = 2;
    private const float MinimumMomentumConfidence = 0.35f;
    private const float HistoryRetentionSeconds = 90f;
    private const float MissingFateRetentionSeconds = 120f;

    private readonly Dictionary<ushort, FateObservationHistory> histories = [];
    private uint territoryId;

    internal void Reset()
    {
        histories.Clear();
        territoryId = 0;
    }

    internal void Observe(IEnumerable<IFate> liveFates, uint currentTerritoryId, DateTime now)
    {
        if (territoryId != currentTerritoryId)
        {
            histories.Clear();
            territoryId = currentTerritoryId;
        }

        var seen = new HashSet<ushort>();
        foreach (var fate in liveFates)
        {
            if (fate.FateId == 0 || fate.State is not (FateState.Running or FateState.Preparing))
                continue;

            seen.Add(fate.FateId);
            if (!histories.TryGetValue(fate.FateId, out var history))
            {
                history = new FateObservationHistory(fate.FateId, fate.StartTimeEpoch, (int)fate.Progress, now);
                histories[fate.FateId] = history;
                continue;
            }

            history.Observe(fate.StartTimeEpoch, (int)fate.Progress, now);
        }

        foreach (var entry in histories.ToArray())
        {
            if (seen.Contains(entry.Key) || (now - entry.Value.LastSeenAt).TotalSeconds <= MissingFateRetentionSeconds)
                continue;
            histories.Remove(entry.Key);
        }
    }

    internal FateProgressObservation GetObservation(ushort fateId, int currentProgress, DateTime now)
    {
        if (!histories.TryGetValue(fateId, out var history))
            return FateProgressObservation.Unknown(fateId, currentProgress);
        return history.CreateSnapshot(currentProgress, now);
    }

    private sealed class FateObservationHistory
    {
        private readonly ushort fateId;
        private readonly List<FateProgressSample> samples = [];
        private int startTimeEpoch;
        private int lastProgress;
        private DateTime lastProgressAt;

        internal FateObservationHistory(ushort fateId, int startTimeEpoch, int progress, DateTime now)
        {
            this.fateId = fateId;
            this.startTimeEpoch = startTimeEpoch;
            lastProgress = progress;
            lastProgressAt = now;
            LastSeenAt = now;
            samples.Add(new(now, progress));
        }

        internal DateTime LastSeenAt { get; private set; }

        internal void Observe(int observedStartTimeEpoch, int progress, DateTime now)
        {
            LastSeenAt = now;
            if ((startTimeEpoch != 0 && observedStartTimeEpoch != 0 && startTimeEpoch != observedStartTimeEpoch)
                || progress < lastProgress)
            {
                Reset(observedStartTimeEpoch, progress, now);
                return;
            }

            if (observedStartTimeEpoch != 0)
                startTimeEpoch = observedStartTimeEpoch;

            if (progress > lastProgress)
            {
                lastProgress = progress;
                lastProgressAt = now;
            }

            var lastSample = samples[^1];
            if (progress != lastSample.Progress || (now - lastSample.ObservedAt).TotalSeconds >= MinimumSampleIntervalSeconds)
                samples.Add(new(now, progress));

            var cutoff = now.AddSeconds(-HistoryRetentionSeconds);
            samples.RemoveAll(sample => sample.ObservedAt < cutoff);
        }

        internal FateProgressObservation CreateSnapshot(int currentProgress, DateTime now)
        {
            if (samples.Count == 0)
                return FateProgressObservation.Unknown(fateId, currentProgress);

            var lastProgressAge = (float)Math.Max(0d, (now - lastProgressAt).TotalSeconds);
            if (lastProgressAge > MomentumStaleAfterSeconds)
                return new(fateId, currentProgress, false, 0f, float.MaxValue, lastProgressAge, 0f, 0, 0f);

            var cutoff = now.AddSeconds(-MomentumWindowSeconds);
            FateProgressSample? baseline = null;
            foreach (var sample in samples)
            {
                if (sample.ObservedAt < cutoff || sample.Progress >= currentProgress)
                    continue;
                baseline = sample;
                break;
            }

            if (baseline is not { } start)
                return new(fateId, currentProgress, false, 0f, float.MaxValue, lastProgressAge, 0f, 0, 0f);

            var spanSeconds = (float)Math.Max(0d, (now - start.ObservedAt).TotalSeconds);
            var progressDelta = currentProgress - start.Progress;
            var progressChanges = 0;
            var previousProgress = start.Progress;
            foreach (var sample in samples)
            {
                if (sample.ObservedAt <= start.ObservedAt || sample.ObservedAt < cutoff)
                    continue;
                if (sample.Progress <= previousProgress)
                    continue;
                progressChanges++;
                previousProgress = sample.Progress;
            }

            if (spanSeconds < MinimumMomentumSpanSeconds
                || progressDelta < MinimumMomentumProgressDelta
                || progressChanges < MinimumMomentumProgressChanges)
            {
                return new(fateId, currentProgress, false, 0f, float.MaxValue, lastProgressAge, spanSeconds, progressDelta, 0f);
            }

            var progressPerSecond = progressDelta / spanSeconds;
            if (progressPerSecond <= 0f)
                return new(fateId, currentProgress, false, 0f, float.MaxValue, lastProgressAge, spanSeconds, progressDelta, 0f);

            var projectedCompletionSeconds = Math.Max(0f, (100f - currentProgress) / progressPerSecond);
            var spanConfidence = Math.Clamp(spanSeconds / 20f, 0f, 1f);
            var deltaConfidence = Math.Clamp(progressDelta / 10f, 0f, 1f);
            var freshnessConfidence = Math.Clamp(1f - (lastProgressAge / MomentumStaleAfterSeconds), 0f, 1f);
            var confidence = (spanConfidence * 0.35f) + (deltaConfidence * 0.4f) + (freshnessConfidence * 0.25f);
            if (confidence < MinimumMomentumConfidence)
                return new(fateId, currentProgress, false, 0f, float.MaxValue, lastProgressAge, spanSeconds, progressDelta, confidence);

            return new(
                fateId,
                currentProgress,
                true,
                progressPerSecond,
                projectedCompletionSeconds,
                lastProgressAge,
                spanSeconds,
                progressDelta,
                confidence);
        }

        private void Reset(int observedStartTimeEpoch, int progress, DateTime now)
        {
            samples.Clear();
            startTimeEpoch = observedStartTimeEpoch;
            lastProgress = progress;
            lastProgressAt = now;
            LastSeenAt = now;
            samples.Add(new(now, progress));
        }
    }

    private readonly record struct FateProgressSample(DateTime ObservedAt, int Progress);
}
