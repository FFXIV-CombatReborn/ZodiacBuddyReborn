namespace ZodiacBuddy.Stages.Novus;

/// <summary>
/// Configuration class for Nexus relic.
/// </summary>
public class NovusConfiguration {
    /// <summary>
    /// Gets or sets a value indicating whether to display the information about equipped relics.
    /// </summary>
    public bool DisplayRelicInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to show the actual numbers in the RelicGlass addon.
    /// </summary>
    public bool ShowNumbersInRelicGlass { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to not display the first message on the RelicGlass addon.
    /// </summary>
    public bool DontPlayRelicGlassAnimation { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to auto-navigate when a map flag is set
    /// (i.e. after the player uses the Alexandrite Map key item and the hint window reveals the location).
    /// </summary>
    public bool AutoNavigateAfterMapUse { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically use the Dig action upon arriving
    /// at the dig location.
    /// </summary>
    public bool AutoDigAfterNavigation { get; set; } = true;
}
