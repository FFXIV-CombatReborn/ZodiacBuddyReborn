using System.Collections.Generic;

using Lumina.Excel.Sheets;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Define the Zenith relic item Id and their names.
/// </summary>
public static class ZenithRelic {
    /// <summary>
    /// List of base relic weapons that can be upgraded to Zenith.
    /// </summary>
    public static readonly Dictionary<uint, string> Items = new() {
        // Base relics (show Zenith upgrade info when these base weapons are equipped)
        { 2046, GetItemName(2046) }, // Curtana (base relic) → Curtana Zenith
        { 2047, GetItemName(2047) }, // Sphairai (base relic) → Sphairai Zenith
        { 2048, GetItemName(2048) }, // Bravura (base relic) → Bravura Zenith
        { 2049, GetItemName(2049) }, // Gae Bolg (base relic) → Gae Bolg Zenith
        { 2050, GetItemName(2050) }, // Artemis Bow (base relic) → Artemis Bow Zenith
        { 2051, GetItemName(2051) }, // Stardust Rod (base relic) → Stardust Rod Zenith
        { 2052, GetItemName(2052) }, // Thyrus (base relic) → Thyrus Zenith
        { 2053, GetItemName(2053) }, // The Veil of Wiyu (base relic) → The Veil of Wiyu Zenith
        { 2054, GetItemName(2054) }, // Omnilex (base relic) → Omnilex Zenith
        { 2055, GetItemName(2055) }, // Holy Shield (base relic) → Holy Shield Zenith
        { 2056, GetItemName(2056) }, // Yoshimitsu (base relic) → Yoshimitsu Zenith
    };

    private static string GetItemName(uint itemId) {
        return Service.DataManager.Excel.GetSheet<Item>()
            .GetRow(itemId).Name
            .ExtractText();
    }
}