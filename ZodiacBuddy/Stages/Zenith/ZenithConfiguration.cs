namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Configuration class for Zodiac Zenith relic.
/// </summary>
public class ZenithConfiguration {
    /// <summary>
    /// Gets or sets a value indicating whether to display the information about the equipped relic.
    /// </summary>
    public bool DisplayRelicInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to show upgrade progress and material tracking.
    /// </summary>
    public bool ShowUpgradeProgress { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to display material requirements.
    /// </summary>
    public bool ShowMaterialRequirements { get; set; } = true;
}