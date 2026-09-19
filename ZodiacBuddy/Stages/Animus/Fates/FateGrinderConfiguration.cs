using System;
using System.Collections.Generic;
using ZodiacBuddy;

namespace ZodiacBuddy.Stages.Animus;

public sealed class FateGrinderConfiguration
{
    public const int RecommendedMinimumTimeRemainingSeconds = 120;
    public const int RecommendedMaximumProgressPercent = 90;
    public const int MaximumConfigurableTimeRemainingSeconds = 3600;
    public const bool RecommendedUseTeleportsWhenBeneficial = true;
    public const bool RecommendedPreferBonusFates = true;
    public const bool RecommendedExperimentalCombat = false;
    public const bool RecommendedMultiPullEnabled = false;

    public int MinimumTimeRemainingSeconds { get; set; } = RecommendedMinimumTimeRemainingSeconds;
    public int MaximumProgressPercent { get; set; } = RecommendedMaximumProgressPercent;
    public bool UseTeleportsWhenBeneficial { get; set; } = RecommendedUseTeleportsWhenBeneficial;
    public bool PreferBonusFates { get; set; } = RecommendedPreferBonusFates;
    public bool ExperimentalCombat { get; set; } = RecommendedExperimentalCombat;
    public bool MultiPullEnabled { get; set; } = RecommendedMultiPullEnabled;
    public int MultiPullRadius { get; set; } = FateGrinderMultiPullProfiles.DefaultPullRadius;
    public int MultiPullHpFloorPercent { get; set; } = FateGrinderMultiPullProfiles.DefaultHpFloorPercent;
    public Dictionary<uint, int> MultiPullPackSizeByJob { get; set; } = new();
    public FateGrinderRsrSettingsBackup RsrSettingsBackup { get; set; } = new();

    internal int EffectiveMinimumTimeRemainingSeconds
        => Math.Clamp(MinimumTimeRemainingSeconds, 0, MaximumConfigurableTimeRemainingSeconds);

    internal int EffectiveMaximumProgressPercent
        => Math.Clamp(MaximumProgressPercent, 0, 100);

    internal float EffectiveMultiPullRadius
        => Math.Clamp(MultiPullRadius, FateGrinderMultiPullProfiles.MinimumPullRadius, FateGrinderMultiPullProfiles.MaximumPullRadius);

    internal int EffectiveMultiPullHpFloorPercent
        => Math.Clamp(MultiPullHpFloorPercent, FateGrinderMultiPullProfiles.MinimumHpFloorPercent, FateGrinderMultiPullProfiles.MaximumHpFloorPercent);

    internal void ResetRecommendedSelectionDefaults()
    {
        MinimumTimeRemainingSeconds = RecommendedMinimumTimeRemainingSeconds;
        MaximumProgressPercent = RecommendedMaximumProgressPercent;
        PreferBonusFates = RecommendedPreferBonusFates;
    }

    internal void ResetRecommendedTravelDefaults()
    {
        UseTeleportsWhenBeneficial = RecommendedUseTeleportsWhenBeneficial;
    }

    internal void ResetMultiPullDefaults()
    {
        MultiPullRadius = FateGrinderMultiPullProfiles.DefaultPullRadius;
        MultiPullHpFloorPercent = FateGrinderMultiPullProfiles.DefaultHpFloorPercent;
        FateGrinderMultiPullProfiles.ResetJobDefaults(this);
    }
}

public sealed class FateGrinderRsrSettingsBackup
{
    public bool IsValid { get; set; }
    public string OriginalHostileType { get; set; } = string.Empty;
    public bool OriginalIgnoreNonFateInFate { get; set; }
    public string AppliedHostileType { get; set; } = string.Empty;
    public bool AppliedIgnoreNonFateInFate { get; set; }
    public bool OriginalForlornPriority { get; set; }
    public bool AppliedForlornPriority { get; set; }
    public bool HasForlornPrioritySnapshot { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.MinValue;

    internal void Capture(RotationSolverGrinderSettings original, RotationSolverGrinderSettings applied)
    {
        IsValid = true;
        OriginalHostileType = original.HostileType;
        OriginalIgnoreNonFateInFate = original.IgnoreNonFateInFate;
        AppliedHostileType = applied.HostileType;
        AppliedIgnoreNonFateInFate = applied.IgnoreNonFateInFate;
        OriginalForlornPriority = original.ForlornPriority;
        AppliedForlornPriority = applied.ForlornPriority;
        HasForlornPrioritySnapshot = true;
        CapturedAt = DateTime.Now;
    }

    internal void Clear()
    {
        IsValid = false;
        OriginalHostileType = string.Empty;
        OriginalIgnoreNonFateInFate = false;
        AppliedHostileType = string.Empty;
        AppliedIgnoreNonFateInFate = false;
        OriginalForlornPriority = false;
        AppliedForlornPriority = false;
        HasForlornPrioritySnapshot = false;
        CapturedAt = DateTime.MinValue;
    }
}
