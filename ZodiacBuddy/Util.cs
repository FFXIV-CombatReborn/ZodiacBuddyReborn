using System;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

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

    /// <summary>
    /// Find the nearest aetheryte to the given map link coordinates.
    /// </summary>
    /// <param name="mapLink">The map link payload to search from.</param>
    /// <returns>The aetheryte row ID, or 0 if none found.</returns>
    internal static uint GetNearestAetheryte(MapLinkPayload mapLink)
    {
        var aetherytes = Service.DataManager.GetExcelSheet<Aetheryte>();
        var mapMarkers = Service.DataManager.GetSubrowExcelSheet<MapMarker>();

        var closestAetheryteId = 0u;
        var closestDistance = double.MaxValue;

        foreach (var aetheryte in aetherytes)
        {
            if (!aetheryte.IsAetheryte)
                continue;

            if (aetheryte.Territory.Value.RowId != mapLink.TerritoryType.RowId)
                continue;

            var map = aetheryte.Map.Value;
            var scale = map.SizeFactor;

            var mapMarker = mapMarkers
                .SelectMany(markers => markers)
                .FirstOrDefault(m => m.DataType == 3 && m.DataKey.RowId == aetheryte.RowId);

            if (mapMarker.RowId is 0)
                continue;

            var aetherX = ConvertRawPositionToMapCoordinate(mapMarker.X, scale);
            var aetherY = ConvertRawPositionToMapCoordinate(mapMarker.Y, scale);

            var distance = Math.Pow(aetherX - mapLink.XCoord, 2) + Math.Pow(aetherY - mapLink.YCoord, 2);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestAetheryteId = aetheryte.RowId;
            }
        }

        return closestAetheryteId;
    }

    private static float ConvertRawPositionToMapCoordinate(int pos, float scale)
    {
        var c = scale / 100.0f;
        var scaledPos = pos * c / 1000.0f;
        return (41.0f / c * ((scaledPos + 1024.0f) / 2048.0f)) + 1.0f;
    }
}
