using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace ZodiacBuddy.Stages.Novus.Data;

/// <summary>
/// A single Alexandrite Map treasure spot.
/// </summary>
internal readonly struct AlexandriteSpot
{
    /// <summary>Gets the in-game zone name (matches the hint window title text).</summary>
    public string ZoneName { get; init; }

    /// <summary>Gets the TerritoryType row ID for the zone.</summary>
    public uint TerritoryId { get; init; }

    /// <summary>Gets the Map row ID for the zone.</summary>
    public uint MapId { get; init; }

    /// <summary>Gets the human-readable map X coordinate.</summary>
    public float MapX { get; init; }

    /// <summary>Gets the human-readable map Y coordinate.</summary>
    public float MapY { get; init; }

    /// <summary>Builds a <see cref="MapLinkPayload"/> for this spot.</summary>
    public MapLinkPayload ToMapLink() => new(TerritoryId, MapId, MapX, MapY);
}

/// <summary>
/// All 34 Alexandrite Map treasure spots (TreasureHuntRank row 6, TreasureSpot subrows 6.0–6.33).
/// Source: TreasureSpot.csv rank-6 subrows → Level.csv world positions, mapped to
/// human-readable map coordinates supplied by the user.
/// The game always has exactly two spots per zone; even subrow indices are "Spot 1" and
/// odd subrow indices are "Spot 2" for each zone pair.
/// </summary>
internal static class AlexandriteMapData
{
    /// <summary>
    /// All 34 spots ordered by TreasureSpot subrow index (6.0 … 6.33).
    /// Can be used when the hint-window AtkValue exposes the subrow index directly.
    /// </summary>
    public static readonly IReadOnlyList<AlexandriteSpot> BySubrow;

    /// <summary>
    /// Spots grouped by zone name (in-game PlaceName string).
    /// Each entry has exactly two elements: [Spot 1, Spot 2].
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AlexandriteSpot[]> ByZoneName;

    static AlexandriteMapData()
    {
        // Ordered by TreasureSpot subrow:  6.0, 6.1, 6.2, …
        // TerritoryId / MapId sourced from Level.csv (via TreasureSpot Level column).
        // MapX / MapY are the human-readable in-game map coordinates.
        var spots = new AlexandriteSpot[]
        {
            // ── The Black Shroud ─────────────────────────────────────────────
            // 6.0 / Level 4629424 / Territory 148
            new() { ZoneName = "Central Shroud",             TerritoryId = 148, MapId =  4, MapX = 10.7f, MapY = 23.6f },
            // 6.1 / Level 4745417 / Territory 148
            new() { ZoneName = "Central Shroud",             TerritoryId = 148, MapId =  4, MapX = 21.3f, MapY = 30.1f },
            // 6.2 / Level 4629432 / Territory 152
            new() { ZoneName = "East Shroud",                TerritoryId = 152, MapId =  5, MapX = 15.6f, MapY = 21.1f },
            // 6.3 / Level 4745435 / Territory 152
            new() { ZoneName = "East Shroud",                TerritoryId = 152, MapId =  5, MapX = 25.4f, MapY = 10.4f },
            // 6.4 / Level 4629437 / Territory 153
            new() { ZoneName = "South Shroud",               TerritoryId = 153, MapId =  6, MapX = 24.3f, MapY = 24.9f },
            // 6.5 / Level 4745457 / Territory 153
            new() { ZoneName = "South Shroud",               TerritoryId = 153, MapId =  6, MapX = 23.9f, MapY = 18.9f },
            // 6.6 / Level 4629442 / Territory 154
            new() { ZoneName = "North Shroud",               TerritoryId = 154, MapId =  7, MapX = 16.2f, MapY = 27.3f },
            // 6.7 / Level 4745466 / Territory 154
            new() { ZoneName = "North Shroud",               TerritoryId = 154, MapId =  7, MapX = 24.0f, MapY = 26.2f },

            // ── La Noscea ───────────────────────────────────────────────────
            // 6.8 / Level 4629447 / Territory 134
            new() { ZoneName = "Middle La Noscea",           TerritoryId = 134, MapId = 15, MapX = 18.0f, MapY = 18.0f },
            // 6.9 / Level 4745479 / Territory 134
            new() { ZoneName = "Middle La Noscea",           TerritoryId = 134, MapId = 15, MapX = 21.8f, MapY = 25.1f },
            // 6.10 / Level 4629452 / Territory 135
            new() { ZoneName = "Lower La Noscea",            TerritoryId = 135, MapId = 16, MapX = 34.5f, MapY = 14.4f },
            // 6.11 / Level 4745488 / Territory 135
            new() { ZoneName = "Lower La Noscea",            TerritoryId = 135, MapId = 16, MapX = 23.6f, MapY = 39.1f },
            // 6.12 / Level 4629457 / Territory 137
            new() { ZoneName = "Eastern La Noscea",          TerritoryId = 137, MapId = 17, MapX = 30.5f, MapY = 27.7f },
            // 6.13 / Level 4745495 / Territory 137
            new() { ZoneName = "Eastern La Noscea",          TerritoryId = 137, MapId = 17, MapX = 17.1f, MapY = 31.3f },
            // 6.14 / Level 4629462 / Territory 138
            new() { ZoneName = "Western La Noscea",          TerritoryId = 138, MapId = 18, MapX = 12.3f, MapY = 35.9f },
            // 6.15 / Level 4745618 / Territory 138
            new() { ZoneName = "Western La Noscea",          TerritoryId = 138, MapId = 18, MapX = 32.8f, MapY = 27.5f },
            // 6.16 / Level 4629467 / Territory 139
            new() { ZoneName = "Upper La Noscea",            TerritoryId = 139, MapId = 19, MapX = 14.2f, MapY = 21.2f },
            // 6.17 / Level 4745637 / Territory 139
            new() { ZoneName = "Upper La Noscea",            TerritoryId = 139, MapId = 19, MapX = 30.6f, MapY = 24.9f },
            // 6.18 / Level 4629472 / Territory 180
            new() { ZoneName = "Outer La Noscea",            TerritoryId = 180, MapId = 30, MapX = 22.9f, MapY = 16.3f },
            // 6.19 / Level 4745664 / Territory 180
            new() { ZoneName = "Outer La Noscea",            TerritoryId = 180, MapId = 30, MapX = 15.3f, MapY = 15.1f },

            // ── Thanalan ────────────────────────────────────────────────────
            // 6.20 / Level 4629477 / Territory 140
            new() { ZoneName = "Western Thanalan",           TerritoryId = 140, MapId = 20, MapX = 21.7f, MapY = 23.3f },
            // 6.21 / Level 4745676 / Territory 140
            new() { ZoneName = "Western Thanalan",           TerritoryId = 140, MapId = 20, MapX = 19.6f, MapY = 26.3f },
            // 6.22 / Level 4629482 / Territory 141
            new() { ZoneName = "Central Thanalan",           TerritoryId = 141, MapId = 21, MapX = 19.2f, MapY = 13.5f },
            // 6.23 / Level 4745727 / Territory 141
            new() { ZoneName = "Central Thanalan",           TerritoryId = 141, MapId = 21, MapX = 29.7f, MapY = 19.4f },
            // 6.24 / Level 4629487 / Territory 145
            new() { ZoneName = "Eastern Thanalan",           TerritoryId = 145, MapId = 22, MapX = 23.3f, MapY = 27.4f },
            // 6.25 / Level 4745771 / Territory 145
            new() { ZoneName = "Eastern Thanalan",           TerritoryId = 145, MapId = 22, MapX = 10.6f, MapY = 18.2f },
            // 6.26 / Level 4629492 / Territory 146
            new() { ZoneName = "Southern Thanalan",          TerritoryId = 146, MapId = 23, MapX = 22.5f, MapY = 38.9f },
            // 6.27 / Level 4745828 / Territory 146
            new() { ZoneName = "Southern Thanalan",          TerritoryId = 146, MapId = 23, MapX = 21.3f, MapY =  8.2f },
            // 6.28 / Level 4629497 / Territory 147
            new() { ZoneName = "Northern Thanalan",          TerritoryId = 147, MapId = 24, MapX = 20.7f, MapY = 27.8f },
            // 6.29 / Level 4745842 / Territory 147
            new() { ZoneName = "Northern Thanalan",          TerritoryId = 147, MapId = 24, MapX = 20.5f, MapY = 22.1f },

            // ── Coerthas / Mor Dhona ─────────────────────────────────────
            // 6.30 / Level 4629502 / Territory 155
            new() { ZoneName = "Coerthas Central Highlands", TerritoryId = 155, MapId = 53, MapX = 23.2f, MapY = 16.0f },
            // 6.31 / Level 4745890 / Territory 155
            new() { ZoneName = "Coerthas Central Highlands", TerritoryId = 155, MapId = 53, MapX = 10.8f, MapY = 27.3f },
            // 6.32 / Level 4629507 / Territory 156
            new() { ZoneName = "Mor Dhona",                  TerritoryId = 156, MapId = 25, MapX = 31.7f, MapY = 13.8f },
            // 6.33 / Level 4745917 / Territory 156
            new() { ZoneName = "Mor Dhona",                  TerritoryId = 156, MapId = 25, MapX = 13.2f, MapY = 10.5f },
        };

        BySubrow = spots;

        // Group pairs into ByZoneName
        var dict = new Dictionary<string, AlexandriteSpot[]>(17);
        for (var i = 0; i < spots.Length; i += 2)
        {
            dict[spots[i].ZoneName] = new[] { spots[i], spots[i + 1] };
        }
        ByZoneName = dict;
    }
}
