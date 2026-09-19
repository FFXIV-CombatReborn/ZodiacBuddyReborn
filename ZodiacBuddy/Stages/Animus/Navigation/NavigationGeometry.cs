using System;
using System.Collections.Generic;
using System.Numerics;

namespace ZodiacBuddy.Stages.Animus;

internal static class NavigationGeometry
{
    internal static float HorizontalDistanceSquared(Vector3 first, Vector3 second)
    {
        var x = first.X - second.X;
        var z = first.Z - second.Z;
        return (x * x) + (z * z);
    }

    internal static bool TryAddDistinctHorizontal(ICollection<Vector3> points, Vector3 candidate, float minimumDistance)
    {
        var minimumDistanceSquared = minimumDistance * minimumDistance;
        foreach (var point in points)
        {
            if (HorizontalDistanceSquared(point, candidate) < minimumDistanceSquared)
                return false;
        }

        points.Add(candidate);
        return true;
    }

    internal static Vector3? ProjectReachableGround(
        Vector3 floorProbe,
        float floorHalfExtent,
        Vector3 reachableProbe,
        float reachableHalfExtent,
        float reachableVerticalHalfExtent,
        out Exception? error,
        float? preferredLayerY = null,
        float maxVerticalDelta = float.PositiveInfinity,
        Vector3? horizontalOrigin = null,
        float maxHorizontalDistance = float.PositiveInfinity)
    {
        error = null;
        if (!VNavmesh.Enabled)
            return null;

        try
        {
            if (!VNavmesh.Nav.IsReady())
                return null;

            var floor = VNavmesh.Query.Mesh.PointOnFloor(floorProbe, false, floorHalfExtent);
            if (floor is Vector3 floorPoint
                && IsProjectionAllowed(floorPoint, preferredLayerY, maxVerticalDelta, horizontalOrigin, maxHorizontalDistance))
                return floorPoint;

            var reachable = VNavmesh.Query.Mesh.NearestPointReachable(reachableProbe, reachableHalfExtent, reachableVerticalHalfExtent);
            if (reachable is Vector3 reachablePoint
                && IsProjectionAllowed(reachablePoint, preferredLayerY, maxVerticalDelta, horizontalOrigin, maxHorizontalDistance))
                return reachablePoint;

            return null;
        }
        catch (Exception exception)
        {
            error = exception;
            return null;
        }
    }

    private static bool IsProjectionAllowed(
        Vector3 point,
        float? preferredLayerY,
        float maxVerticalDelta,
        Vector3? horizontalOrigin,
        float maxHorizontalDistance)
    {
        if (preferredLayerY is float layerY
            && float.IsFinite(maxVerticalDelta)
            && MathF.Abs(point.Y - layerY) > maxVerticalDelta)
            return false;

        if (horizontalOrigin is Vector3 origin
            && float.IsFinite(maxHorizontalDistance)
            && HorizontalDistanceSquared(point, origin) > maxHorizontalDistance * maxHorizontalDistance)
            return false;

        return true;
    }
}
