using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Defines navigation destinations for the Zenith stage.
/// </summary>
public static class ZenithDestinations {
    /// <summary>
    /// The Furnace location in North Shroud (Hyrstmill) where Zenith weapons are upgraded.
    /// Territory 154 = North Shroud, Aetheryte 7 = Fallgourd Float
    /// </summary>
    public static readonly MapLinkPayload Furnace = new(154, 7, 30.0f, 20.0f);
    
    /// <summary>
    /// Auriana in Mor Dhona - Primary vendor for Thavnairian Mist (Allagan Tomestones of Poetics).
    /// Territory 156 = Mor Dhona, Aetheryte 11 = Revenant's Toll
    /// </summary>
    public static readonly MapLinkPayload AurianaPoeticsVendor = new(156, 11, 22.7f, 6.7f);
    
    /// <summary>
    /// Hismena in Idyllshire - Alternative vendor for Thavnairian Mist (Allagan Tomestones of Poetics).
    /// Territory 478 = Idyllshire, Aetheryte 75 = Idyllshire
    /// </summary>
    public static readonly MapLinkPayload HismenaPoeticsVendor = new(478, 75, 5.7f, 5.2f);

    /// <summary>
    /// Display names for destinations.
    /// </summary>
    public static class Names {
        public const string Furnace = "The Furnace (Hyrstmill, North Shroud)";
        public const string AurianaPoeticsVendor = "Auriana (Mor Dhona) - Poetics Vendor";
        public const string HismenaPoeticsVendor = "Hismena (Idyllshire) - Poetics Vendor";
    }
}