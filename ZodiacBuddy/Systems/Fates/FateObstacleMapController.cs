using System;
using System.Numerics;
using System.Threading.Tasks;

namespace ZodiacBuddy.Systems.Fates;

internal sealed class FateObstacleMapController
{
    private const double GenerationTimeoutSeconds = 5d;
    private const float GeneralGenerationMargin = 50f;
    private const float EscortGenerationMargin = 2f;
    private const float MinimumGenerationRadius = 10f;
    private const float SeedSearchVertical = 64f;
    private const float PreferredSeedSearchHorizontal = 8f;
    private const float PreferredSeedSearchVertical = 24f;

    private ObstacleMapState state;
    private ushort workingFateId;
    private uint territoryId;
    private Vector3 requestedCenter;
    private Vector3 generationCenter;
    private float fateRadius;
    private Vector3? preferredSeed;
    private string preferredSeedSource = "loaded FATE objective";
    private float generationRadius;
    private float generationMargin = GeneralGenerationMargin;
    private DateTime generationStartedAt;
    private DateTime generationDeadline;
    private bool ownsTempMap;
    private bool generationRequestOwned;
    private bool supersededGenerationPending;
    private DateTime escortRefreshRetryAt = DateTime.MinValue;

    internal string Status { get; private set; } = string.Empty;
    internal bool HasOwnedTempMap => ownsTempMap;
    internal bool NeedsPreparation(ushort fateId)
        => workingFateId != fateId || (state != ObstacleMapState.Ready && state != ObstacleMapState.FailedOpen);

    internal bool NeedsEscortRefresh(Vector3 escortAnchorPosition, float recenterDistance)
        => ownsTempMap
            && state == ObstacleMapState.Ready
            && recenterDistance > 0f
            && DateTime.Now >= escortRefreshRetryAt
            && IsFinite(escortAnchorPosition)
            && HorizontalDistance(escortAnchorPosition, requestedCenter) >= recenterDistance;

    internal void BeginWorkingFate(ushort fateId)
    {
        if (workingFateId == fateId)
            return;

        if (ownsTempMap)
            ClearOwnedTempMap();

        supersededGenerationPending = false;
        if (generationRequestOwned)
        {
            if (BossModIPC.TryGetObstacleMapGenerationStatus(out var priorStatus) && IsTerminal(priorStatus))
            {
                if (priorStatus == TaskStatus.RanToCompletion)
                    BossModIPC.TryClearTempObstacleMap();
                generationRequestOwned = false;
            }
            else
            {
                supersededGenerationPending = true;
            }
        }

        workingFateId = fateId;
        territoryId = 0;
        requestedCenter = default;
        generationCenter = default;
        fateRadius = 0f;
        preferredSeed = null;
        preferredSeedSource = "loaded FATE objective";
        generationRadius = 0f;
        generationMargin = GeneralGenerationMargin;
        generationStartedAt = DateTime.MinValue;
        generationDeadline = supersededGenerationPending ? DateTime.Now.AddSeconds(GenerationTimeoutSeconds) : DateTime.MinValue;
        ownsTempMap = false;
        escortRefreshRetryAt = DateTime.MinValue;
        state = supersededGenerationPending ? ObstacleMapState.WaitingForSupersededGeneration : ObstacleMapState.Idle;
        Status = string.Empty;
    }

    internal bool EnsureReady(
        ushort fateId,
        string fateName,
        uint currentTerritoryId,
        Vector3 center,
        float radius,
        Vector3? preferredSeed = null,
        string preferredSeedSource = "loaded FATE objective")
    {
        BeginWorkingFate(fateId);

        switch (state)
        {
            case ObstacleMapState.Ready:
            case ObstacleMapState.FailedOpen:
                return true;
            case ObstacleMapState.WaitingForGenerator:
                return TickWaitingForGenerator(fateName);
            case ObstacleMapState.WaitingForSupersededGeneration:
                return TickSupersededGeneration(fateName);
            case ObstacleMapState.Generating:
                return TickGeneration(fateName);
            case ObstacleMapState.Idle:
                return StartGeneration(fateId, fateName, currentTerritoryId, center, radius, preferredSeed, preferredSeedSource, GeneralGenerationMargin);
            default:
                return true;
        }
    }

    internal bool BeginEscortRefresh(ushort fateId, string fateName, uint currentTerritoryId, Vector3 escortAnchorPosition, float localRadius)
    {
        if (workingFateId != fateId
            || state != ObstacleMapState.Ready
            || !ownsTempMap
            || !IsFinite(escortAnchorPosition))
            return false;

        var displacement = HorizontalDistance(escortAnchorPosition, requestedCenter);
        if (!BossModIPC.TryClearTempObstacleMap())
        {
            escortRefreshRetryAt = DateTime.Now.AddSeconds(2);
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not recenter owned obstacle map for Escort FateId={workingFateId}; retrying shortly.");
            return false;
        }

        escortRefreshRetryAt = DateTime.MinValue;
        ownsTempMap = false;
        generationRequestOwned = false;
        state = ObstacleMapState.Idle;
        Status = $"Recentering the local terrain map around the moving escort in {fateName} ({fateId}).";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Recentering Escort FateId={fateId} obstacle map after {displacement:F1}y movement.");

        _ = StartGeneration(
            fateId,
            fateName,
            currentTerritoryId,
            escortAnchorPosition,
            Math.Max(MinimumGenerationRadius, localRadius),
            escortAnchorPosition,
            "escort anchor",
            EscortGenerationMargin);

        return state is ObstacleMapState.WaitingForGenerator
            or ObstacleMapState.WaitingForSupersededGeneration
            or ObstacleMapState.Generating;
    }

    internal bool SuppressForWorkingFate(string reason)
    {
        if (!ownsTempMap)
            return false;

        if (!BossModIPC.TryClearTempObstacleMap())
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not suppress owned obstacle map for FateId={workingFateId}; clear failed.");
            return false;
        }

        ownsTempMap = false;
        generationRequestOwned = false;
        state = ObstacleMapState.FailedOpen;
        Status = $"Local terrain map suppressed for FateId={workingFateId}; continuing without the ZBR temporary map.";
        Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Obstacle map suppressed for FateId={workingFateId}: {reason}.");
        return true;
    }

    internal void Reset()
    {
        if (ownsTempMap)
            ClearOwnedTempMap();
        else if (generationRequestOwned
            && BossModIPC.TryGetObstacleMapGenerationStatus(out var status)
            && status == TaskStatus.RanToCompletion
            && BossModIPC.TryHasTempObstacleMap(out var hasTempMap)
            && hasTempMap)
            BossModIPC.TryClearTempObstacleMap();

        state = ObstacleMapState.Idle;
        workingFateId = 0;
        territoryId = 0;
        requestedCenter = default;
        generationCenter = default;
        fateRadius = 0f;
        preferredSeed = null;
        preferredSeedSource = "loaded FATE objective";
        generationRadius = 0f;
        generationMargin = GeneralGenerationMargin;
        generationStartedAt = DateTime.MinValue;
        generationDeadline = DateTime.MinValue;
        ownsTempMap = false;
        generationRequestOwned = false;
        supersededGenerationPending = false;
        escortRefreshRetryAt = DateTime.MinValue;
        Status = string.Empty;
    }

    private bool StartGeneration(
        ushort fateId,
        string fateName,
        uint currentTerritoryId,
        Vector3 center,
        float radius,
        Vector3? seedHint,
        string seedHintSource,
        float margin)
    {
        territoryId = currentTerritoryId;
        requestedCenter = center;
        fateRadius = Math.Max(1f, radius);
        preferredSeed = seedHint;
        preferredSeedSource = seedHintSource;
        generationMargin = Math.Max(0f, margin);
        if (!TryResolveGenerationCenter(center, fateRadius, preferredSeed, preferredSeedSource, out generationCenter, out var seedSource))
            return FailOpen(fateName, $"no reachable navmesh seed was found within the FATE area around {FormatPosition(center)}");

        var centerOffset = HorizontalDistance(generationCenter, requestedCenter);
        generationRadius = Math.Max(MinimumGenerationRadius, fateRadius + centerOffset + generationMargin);
        generationStartedAt = DateTime.Now;
        generationDeadline = generationStartedAt.AddSeconds(GenerationTimeoutSeconds);

        if (BossModIPC.TryGenerateTemporaryObstacleMap(generationCenter, generationRadius))
        {
            generationRequestOwned = true;
            state = ObstacleMapState.Generating;
            Status = $"Generating local terrain map for {fateName} ({fateId}).";
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle map requested for FateId={fateId}; seed={seedSource}, radius={generationRadius:F1}y.");
            return false;
        }

        if (BossModIPC.TryGetObstacleMapGenerationStatus(out var existingStatus) && !IsTerminal(existingStatus))
        {
            state = ObstacleMapState.WaitingForGenerator;
            Status = $"Waiting for BMR obstacle-map generation before preparing {fateName} ({fateId}).";
            return false;
        }

        return FailOpen(fateName, "generation IPC was unavailable or rejected");
    }

    private bool TickGeneration(string fateName)
    {
        var now = DateTime.Now;
        if (now >= generationDeadline)
            return FailOpen(fateName, $"generation did not complete within {GenerationTimeoutSeconds:F0}s");

        if (!BossModIPC.TryGetObstacleMapGenerationStatus(out var status))
            return FailOpen(fateName, "generation status IPC became unavailable");

        if (!IsTerminal(status))
            return false;

        if (status != TaskStatus.RanToCompletion)
            return FailOpen(fateName, $"generation ended with status {status}");

        if (!BossModIPC.TryHasTempObstacleMap(out var hasTempMap) || !hasTempMap)
            return FailOpen(fateName, "generation completed without a temporary map being available");

        ownsTempMap = true;
        state = ObstacleMapState.Ready;
        var elapsedMs = Math.Max(0d, (now - generationStartedAt).TotalMilliseconds);
        Status = $"Local terrain map ready for {fateName} ({workingFateId}).";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle map ready for FateId={workingFateId} after {elapsedMs:F0}ms.");
        return true;
    }

    private bool TickSupersededGeneration(string fateName)
    {
        var now = DateTime.Now;
        if (!BossModIPC.TryGetObstacleMapGenerationStatus(out var status))
            return FailOpen(fateName, "could not observe a superseded obstacle-map generation");

        if (!IsTerminal(status))
        {
            if (now < generationDeadline)
                return false;
            return FailOpen(fateName, $"a superseded obstacle-map generation did not finish within {GenerationTimeoutSeconds:F0}s");
        }

        if (supersededGenerationPending && status == TaskStatus.RanToCompletion)
            BossModIPC.TryClearTempObstacleMap();

        supersededGenerationPending = false;
        generationRequestOwned = false;
        state = ObstacleMapState.Idle;
        generationDeadline = DateTime.MinValue;
        Status = string.Empty;
        return false;
    }

    private bool TickWaitingForGenerator(string fateName)
    {
        var now = DateTime.Now;
        if (now >= generationDeadline)
            return FailOpen(fateName, $"BMR obstacle-map generator remained busy for {GenerationTimeoutSeconds:F0}s");

        if (!BossModIPC.TryGetObstacleMapGenerationStatus(out var status))
            return FailOpen(fateName, "generation status IPC became unavailable while waiting for the generator");

        if (!IsTerminal(status))
            return false;

        state = ObstacleMapState.Idle;
        generationDeadline = DateTime.MinValue;
        Status = string.Empty;
        return StartGeneration(workingFateId, fateName, territoryId, requestedCenter, fateRadius, preferredSeed, preferredSeedSource, generationMargin);
    }

    private bool FailOpen(string fateName, string reason)
    {
        state = ObstacleMapState.FailedOpen;
        Status = $"Local terrain map unavailable for {fateName} ({workingFateId}); continuing with normal FATE recovery.";
        Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Obstacle map unavailable for FateId={workingFateId}: {reason}; continuing without it.");
        return true;
    }

    private void ClearOwnedTempMap()
    {
        BossModIPC.TryClearTempObstacleMap();
        ownsTempMap = false;
        generationRequestOwned = false;
    }

    private static bool TryResolveGenerationCenter(Vector3 center, float radius, Vector3? preferredSeed, string preferredSeedSource, out Vector3 resolved, out string source)
    {
        resolved = default;
        source = string.Empty;
        try
        {
            if (preferredSeed is Vector3 preferred && IsFinite(preferred))
            {
                var preferredGround = VNavmesh.Query.Mesh.NearestPointReachable(preferred, PreferredSeedSearchHorizontal, PreferredSeedSearchVertical);
                if (preferredGround is Vector3 reachablePreferred && IsFinite(reachablePreferred))
                {
                    resolved = reachablePreferred;
                    source = preferredSeedSource;
                    return true;
                }
            }

            var searchHorizontal = Math.Max(MinimumGenerationRadius, radius);
            var centerSeed = VNavmesh.Query.Mesh.NearestPointReachable(center, searchHorizontal, SeedSearchVertical);
            if (centerSeed is Vector3 reachableCenter && IsFinite(reachableCenter))
            {
                resolved = reachableCenter;
                source = HorizontalDistance(reachableCenter, center) <= 0.5f ? "FATE center" : "reachable ground near FATE center";
                return true;
            }
        }
        catch (Exception exception)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle-map seed resolution failed: {exception.Message}");
        }

        return false;
    }

    private static bool IsTerminal(TaskStatus status)
        => status is TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static string FormatPosition(Vector3 position)
        => $"<{position.X:F1}, {position.Y:F1}, {position.Z:F1}>";

    private enum ObstacleMapState
    {
        Idle,
        WaitingForGenerator,
        WaitingForSupersededGeneration,
        Generating,
        Ready,
        FailedOpen,
    }
}
