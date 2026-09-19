using Dalamud.Utility;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace ZodiacBuddy.Stages.Animus;

internal static class OuterLaNosceaTravelPolicy
{
    internal const uint TerritoryTypeId = 180;
    internal const float GroundTravelBoundaryZ = -470f;
    private const uint MapId = 30;
    private const float UGhamaroMapMinX = 21.5f;
    private const float UGhamaroMapMaxX = 31.5f;
    private const float UGhamaroMapMinY = 3.5f;
    private const float UGhamaroMapMaxY = 12.5f;

    internal static bool IsUGhamaroMapLocation(float mapX, float mapY)
        => float.IsFinite(mapX)
            && float.IsFinite(mapY)
            && mapX >= UGhamaroMapMinX
            && mapX <= UGhamaroMapMaxX
            && mapY >= UGhamaroMapMinY
            && mapY <= UGhamaroMapMaxY;

    internal static bool IsUGhamaroWorldLocation(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z))
            return false;

        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(MapId);
        var mapX = MapUtil.ConvertWorldCoordXZToMapCoord(position.X, map.SizeFactor, map.OffsetX);
        var mapY = MapUtil.ConvertWorldCoordXZToMapCoord(position.Z, map.SizeFactor, map.OffsetY);
        return IsUGhamaroMapLocation(mapX, mapY);
    }

    internal static bool HasCrossedGroundTravelBoundary(Vector3 position)
        => float.IsFinite(position.Z) && position.Z <= GroundTravelBoundaryZ;

    internal static bool HasExitedGroundTravelBoundary(Vector3 position)
        => float.IsFinite(position.Z) && position.Z > GroundTravelBoundaryZ;
}
