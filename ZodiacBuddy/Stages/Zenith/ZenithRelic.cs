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
        // Zenith weapons (placeholder IDs - need to be updated with correct values)
        { 6565, GetItemName(6565) }, // Curtana Zenith
        { 6566, GetItemName(6566) }, // Sphairai Zenith
        { 6567, GetItemName(6567) }, // Bravura Zenith
        { 6568, GetItemName(6568) }, // Gae Bolg Zenith
        { 6569, GetItemName(6569) }, // Artemis Bow Zenith
        { 6570, GetItemName(6570) }, // Thyrse Zenith
        { 6571, GetItemName(6571) }, // Stardust Rod Zenith
        { 6572, GetItemName(6572) }, // The Veil of Wiyu Zenith
        { 6573, GetItemName(6573) }, // Omnilex Zenith
        { 6574, GetItemName(6574) }, // Holy Shield Zenith
        { 6575, GetItemName(6575) }, // Yoshimitsu Zenith
        
        // Base relics (for testing purposes - will show Zenith info for base weapons)
        // TODO: Remove these once correct Zenith IDs are found
        { 2052, GetItemName(2052) }, // Thyrus (base relic for testing) - CORRECT ID
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