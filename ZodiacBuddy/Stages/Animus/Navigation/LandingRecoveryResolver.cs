using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ZodiacBuddy.Stages.Animus;

internal enum LandingRecoveryPreference
{
    NearestToPlayer,
    BeyondAnchorFromPlayer,
}

internal static class LandingRecoveryResolver
{
    private static readonly Vector2[] Directions =
    [
        new(1f, 0f),
        new(-1f, 0f),
        new(0f, 1f),
        new(0f, -1f),
        Vector2.Normalize(new Vector2(1f, 1f)),
        Vector2.Normalize(new Vector2(-1f, 1f)),
        Vector2.Normalize(new Vector2(1f, -1f)),
        Vector2.Normalize(new Vector2(-1f, -1f)),
    ];

    internal static Vector3? ResolveNearbyGround(
        Vector3 playerPosition,
        Vector3 anchor,
        int attempt,
        float baseRadius,
        LandingRecoveryPreference preference,
        out Exception? error)
        => ResolveNearbyGround(playerPosition, anchor, attempt, baseRadius, preference, null, float.PositiveInfinity, out error);

    internal static Vector3? ResolveNearbyGround(
        Vector3 playerPosition,
        Vector3 anchor,
        int attempt,
        float baseRadius,
        LandingRecoveryPreference preference,
        float? preferredLayerY,
        float maxVerticalDelta,
        out Exception? error)
    {
        error = null;
        if (!VNavmesh.Enabled)
            return null;
        try
        {
            if (!VNavmesh.Nav.IsReady())
                return null;
        }
        catch (Exception exception)
        {
            error = exception;
            return null;
        }

        var candidates = new List<Vector3>();
        var radius = baseRadius + attempt * 4f;
        var layerAware = preferredLayerY.HasValue && float.IsFinite(maxVerticalDelta) && maxVerticalDelta > 0f;
        var layerY = preferredLayerY.GetValueOrDefault();

        foreach (var direction in Directions)
        {
            var probeY = layerAware ? layerY + MathF.Min(3f, maxVerticalDelta * 0.5f) : 1024f;
            var probe = new Vector3(anchor.X + direction.X * radius, probeY, anchor.Z + direction.Y * radius);
            var resolved = NavigationGeometry.ProjectReachableGround(
                probe,
                4f,
                probe,
                4f,
                layerAware ? maxVerticalDelta : 2048f,
                out var projectionError,
                layerAware ? layerY : null,
                layerAware ? maxVerticalDelta : float.PositiveInfinity);
            if (projectionError != null)
            {
                error = projectionError;
                return null;
            }
            if (resolved is not Vector3 point)
                continue;
            if (NavigationGeometry.HorizontalDistanceSquared(point, playerPosition) < 16f)
                continue;
            if (NavigationGeometry.HorizontalDistanceSquared(point, anchor) > 625f)
                continue;
            NavigationGeometry.TryAddDistinctHorizontal(candidates, point, 2f);
        }

        if (candidates.Count == 0)
            return null;

        if (preference == LandingRecoveryPreference.BeyondAnchorFromPlayer)
        {
            var approach = new Vector2(anchor.X - playerPosition.X, anchor.Z - playerPosition.Z);
            if (approach.LengthSquared() > 0.01f)
            {
                approach = Vector2.Normalize(approach);
                var beyond = candidates
                    .Select(point => new
                    {
                        Point = point,
                        Score = Vector2.Dot(new Vector2(point.X - anchor.X, point.Z - anchor.Z), approach),
                    })
                    .Where(candidate => candidate.Score >= MathF.Max(2f, radius * 0.25f))
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => NavigationGeometry.HorizontalDistanceSquared(candidate.Point, anchor))
                    .FirstOrDefault();
                return beyond?.Point;
            }
        }

        return candidates.OrderBy(point => NavigationGeometry.HorizontalDistanceSquared(point, playerPosition)).First();
    }
}
