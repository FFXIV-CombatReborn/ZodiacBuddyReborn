using System.Collections.Generic;

using Lumina.Excel.Sheets;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Define the Zenith relic item Id and their names.
/// </summary>
public static class ZenithRelic {
    /// <summary>
    /// List of Zodiac Zenith weapons and base relics (for testing).
    /// </summary>
    public static readonly Dictionary<uint, string> Items = new() {
        // Zenith weapons (correct IDs)
        { 6257, GetItemName(6257) }, // Curtana Zenith
        { 6258, GetItemName(6258) }, // Sphairai Zenith
        { 6259, GetItemName(6259) }, // Bravura Zenith
        { 6260, GetItemName(6260) }, // Gae Bolg Zenith
        { 6261, GetItemName(6261) }, // Artemis Bow Zenith
        { 6262, GetItemName(6262) }, // Thyrus Zenith
        { 6263, GetItemName(6263) }, // Stardust Rod Zenith
        { 6264, GetItemName(6264) }, // The Veil of Wiyu Zenith
        { 6265, GetItemName(6265) }, // Omnilex Zenith
        { 6266, GetItemName(6266) }, // Holy Shield Zenith
        { 9250, GetItemName(9250) }, // Yoshimitsu Zenith
        
        // Base relics (show Zenith info when base weapons are equipped)
        { 2052, GetItemName(2052) }, // Thyrus (base relic)
        { 2046, GetItemName(2046) }, // Curtana (base relic)
        { 2047, GetItemName(2047) }, // Sphairai (base relic)
        { 2048, GetItemName(2048) }, // Bravura (base relic)
        { 2049, GetItemName(2049) }, // Gae Bolg (base relic)
        { 2050, GetItemName(2050) }, // Artemis Bow (base relic)
        { 2051, GetItemName(2051) }, // Stardust Rod (base relic)
        { 2053, GetItemName(2053) }, // The Veil of Wiyu (base relic)
        { 2054, GetItemName(2054) }, // Omnilex (base relic)
        { 2055, GetItemName(2055) }, // Holy Shield (base relic)
        { 2056, GetItemName(2056) }, // Yoshimitsu (base relic)
    };

    private static string GetItemName(uint itemId) {
        return Service.DataManager.Excel.GetSheet<Item>()
            .GetRow(itemId).Name
            .ExtractText();
    }
}