using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ZodiacBuddy.Stages.Animus.Data;

internal sealed class MobSpawnResolver
{
    internal const float MinimumRelocationDistance = 12f;
    private const float CandidateMergeDistance = 4f;
    private const float ProjectionProbeY = 1024f;
    private const float FloorSearchHalfExtent = 6f;
    private const float ReachableSearchHalfExtent = 8f;
    private const float ReachableSearchHalfExtentY = 2048f;
    private const float MaximumProjectionDistance = 12f;
    private readonly Dictionary<(uint BNpcNameId, uint TerritoryTypeId), MobSpawnPosition[]> spawnsByTarget = [];

    public MobSpawnResolver()
    {
        try
        {
            var spawns = CsvLoader.LoadResource<MobSpawnPosition>(
                CsvLoader.MobSpawnResourceName,
                true,
                out var failedLines,
                out var exceptions);

            spawnsByTarget = spawns
                .Where(spawn => spawn.BNpcNameId != 0 && spawn.TerritoryTypeId != 0)
                .GroupBy(spawn => (spawn.BNpcNameId, spawn.TerritoryTypeId))
                .ToDictionary(group => group.Key, group => group.ToArray());

            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Loaded {spawns.Count} LuminaSupplemental mob spawns across {spawnsByTarget.Count} target/territory pairs.");
            if (failedLines.Count > 0 || exceptions.Count > 0)
                Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] LuminaSupplemental mob-spawn load skipped {failedLines.Count} rows with {exceptions.Count} parse errors.");
        }
        catch (Exception exception)
        {
            Service.PluginLog.Error(exception, "[ZodiacBuddy/ENEMY] Failed to load LuminaSupplemental mob spawns.");
        }
    }

    public MapLinkPayload? GetPreferredMapLink(EnemyObjectiveDefinition target)
    {
        var mapPoints = GetSpawns(target)
            .Select(spawn => TryGetMapCoordinates(target, spawn, out var mapPoint) ? mapPoint : (Vector2?)null)
            .Where(mapPoint => mapPoint.HasValue)
            .Select(mapPoint => mapPoint!.Value)
            .ToArray();
        if (mapPoints.Length == 0)
            return null;

        var referenceX = target.Position.XCoord;
        var referenceY = target.Position.YCoord;
        var best = mapPoints
            .OrderBy(mapPoint => DistanceSquared(referenceX, referenceY, mapPoint.X, mapPoint.Y))
            .First();
        return CreateMapLink(target, best.X, best.Y);
    }

    public bool TryResolvePreferredWorldDestination(
        EnemyObjectiveDefinition target,
        MapLinkPayload preferredMapLink,
        out Vector3 destination,
        out int candidateCount)
    {
        destination = default;
        var candidates = ResolveCandidates(target);
        candidateCount = candidates.Count;
        if (candidateCount == 0)
            return false;

        var preferredPoint = new Vector3(preferredMapLink.RawX / 1000f, 0f, preferredMapLink.RawY / 1000f);
        destination = candidates
            .OrderBy(candidate => NavigationGeometry.HorizontalDistanceSquared(candidate, preferredPoint))
            .First();
        return true;
    }

    public bool TryResolveWorldDestination(
        EnemyObjectiveDefinition target,
        Vector3 origin,
        IReadOnlyCollection<Vector3>? excludedDestinations,
        out Vector3 destination,
        out int candidateCount)
    {
        destination = default;
        var candidates = ResolveCandidates(target);
        candidateCount = candidates.Count;
        if (candidateCount == 0)
            return false;

        IEnumerable<Vector3> available = candidates;
        if (excludedDestinations is { Count: > 0 })
        {
            var minimumDistanceSquared = MinimumRelocationDistance * MinimumRelocationDistance;
            available = candidates.Where(candidate => excludedDestinations.All(excluded => NavigationGeometry.HorizontalDistanceSquared(candidate, excluded) >= minimumDistanceSquared));
        }

        var ordered = available
            .OrderBy(candidate => NavigationGeometry.HorizontalDistanceSquared(origin, candidate))
            .ToArray();
        if (ordered.Length == 0)
            return false;

        destination = ordered[0];
        return true;
    }

    public bool TryResolveBookFallback(EnemyObjectiveDefinition target, out Vector3 destination)
    {
        destination = default;
        var mapLink = target.Position;
        var horizontalPoint = new Vector3(mapLink.RawX / 1000f, ProjectionProbeY, mapLink.RawY / 1000f);
        var meshPoint = ResolveReachableMeshPoint(horizontalPoint);
        if (!meshPoint.HasValue)
            return false;

        destination = meshPoint.Value;
        return true;
    }

    private List<Vector3> ResolveCandidates(EnemyObjectiveDefinition target)
    {
        var candidates = new List<Vector3>();
        foreach (var spawn in GetSpawns(target))
        {
            if (!TryGetMapCoordinates(target, spawn, out var mapPoint))
                continue;

            var mapLink = CreateMapLink(target, mapPoint.X, mapPoint.Y);
            var horizontalPoint = new Vector3(mapLink.RawX / 1000f, ProjectionProbeY, mapLink.RawY / 1000f);
            var meshPoint = ResolveReachableMeshPoint(horizontalPoint);
            if (!meshPoint.HasValue)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Rejected LuminaSupplemental spawn for objective {target.MonsterNoteTargetId} at map ({mapPoint.X:F1},{mapPoint.Y:F1}); no reachable navmesh geometry was found nearby.");
                continue;
            }

            if (!NavigationGeometry.TryAddDistinctHorizontal(candidates, meshPoint.Value, CandidateMergeDistance))
                continue;
        }

        return candidates;
    }

    private MobSpawnPosition[] GetSpawns(EnemyObjectiveDefinition target)
        => spawnsByTarget.TryGetValue((target.BNpcNameId, target.Position.TerritoryType.RowId), out var spawns)
            ? spawns
            : [];

    private static bool TryGetMapCoordinates(EnemyObjectiveDefinition target, MobSpawnPosition spawn, out Vector2 mapPoint)
    {
        var position = spawn.Position;
        if (IsMapCoordinate(position.X) && IsMapCoordinate(position.Y))
        {
            mapPoint = new Vector2(position.X, position.Y);
            return true;
        }

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z))
        {
            mapPoint = default;
            return false;
        }

        var map = target.Position.Map.Value;
        var mapX = MapUtil.ConvertWorldCoordXZToMapCoord(position.X, map.SizeFactor, map.OffsetX);
        var mapY = MapUtil.ConvertWorldCoordXZToMapCoord(position.Z, map.SizeFactor, map.OffsetY);
        mapPoint = new Vector2(mapX, mapY);
        return IsMapCoordinate(mapX) && IsMapCoordinate(mapY);
    }

    private static MapLinkPayload CreateMapLink(EnemyObjectiveDefinition target, float mapX, float mapY)
        => new(target.Position.TerritoryType.RowId, target.Position.Map.RowId, mapX, mapY, 0f);

    private static Vector3? ResolveReachableMeshPoint(Vector3 horizontalPoint)
    {
        var resolved = NavigationGeometry.ProjectReachableGround(
            horizontalPoint,
            FloorSearchHalfExtent,
            horizontalPoint,
            ReachableSearchHalfExtent,
            ReachableSearchHalfExtentY,
            out var error,
            horizontalOrigin: horizontalPoint,
            maxHorizontalDistance: MaximumProjectionDistance);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Could not project a LuminaSupplemental mob spawn onto reachable navmesh: {error.Message}");
        return resolved;
    }

    private static bool IsMapCoordinate(float coordinate)
        => float.IsFinite(coordinate) && coordinate is >= 0f and <= 100f;

    private static float DistanceSquared(float x1, float y1, float x2, float y2)
    {
        var x = x1 - x2;
        var y = y1 - y2;
        return (x * x) + (y * y);
    }

}
