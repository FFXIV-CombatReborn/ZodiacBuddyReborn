using System;
using System.Globalization;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ZodiacBuddy;

/// <summary>
/// Utility methods.
/// </summary>
internal static class Util {
    /// <summary>
    /// Return the item equipped on the slot id.
    /// </summary>
    /// <param name="index">Slot index of the desired item.</param>
    /// <returns>Equipped item on the slot or the default item 0.</returns>
    public static unsafe InventoryItem GetEquippedItem(int index) {
        var im = InventoryManager.Instance();
        if (im == null)
            throw new Exception("InventoryManager was null");

        var equipped = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null)
            throw new Exception("EquippedItems was null");

        var slot = equipped->GetInventorySlot(index);
        if (slot == null)
            throw new Exception($"InventorySlot{index} was null");

        return *slot;
    }

    public static string SmartTitleCase(string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (Regex.IsMatch(words[i], @"^\d+(st|nd|rd|th)$", RegexOptions.IgnoreCase))
            {
                words[i] = words[i].ToLowerInvariant();
            }
            else
            {
                words[i] = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(words[i].ToLowerInvariant());
            }
        }

        return string.Join(' ', words);
    }
}
