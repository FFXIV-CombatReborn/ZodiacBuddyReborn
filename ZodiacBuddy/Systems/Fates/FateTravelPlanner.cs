using ECommons.DalamudServices;
using System;
using System.Numerics;
using EcMap = ECommons.GameHelpers.Map;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateTravelMethod
{
    Direct,
    Aetheryte,
}

internal readonly record struct FateTravelPlan(
    FateTravelMethod Method,
    uint BestAetheryteId,
    string BestAetheryteName,
    Vector3 BestAetherytePosition,
    float DirectDistance,
    float PostTeleportDistance,
    float EstimatedDirectSeconds,
    float EstimatedTeleportSeconds,
    float EstimatedSelectedSeconds,
    float EstimatedSavingsSeconds,
    string DecisionReason)
{
    internal bool UsesTeleport => Method == FateTravelMethod.Aetheryte && BestAetheryteId != 0;
}

internal static class FateTravelPlanner
{
    private const float EstimatedTravelYalmsPerSecond = 10f;
    private const float EstimatedTeleportOverheadSeconds = 18f;
    private const float MinimumTeleportSavingsSeconds = 10f;

    internal static FateTravelPlan Plan(
        uint territoryId,
        Vector3 playerPosition,
        Vector3 targetPosition,
        bool allowTeleport)
    {
        var directDistance = Vector3.Distance(playerPosition, targetPosition);
        var estimatedDirectSeconds = EstimateMovementSeconds(directDistance);

        if (!TryFindBestUnlockedAetheryte(territoryId, targetPosition, out var aetheryteId, out var aetheryteName, out var aetherytePosition, out var postTeleportDistance))
        {
            return new(
                FateTravelMethod.Direct,
                0,
                string.Empty,
                default,
                directDistance,
                float.MaxValue,
                estimatedDirectSeconds,
                float.MaxValue,
                estimatedDirectSeconds,
                0f,
                "no unlocked same-territory aetheryte was available");
        }

        var estimatedTeleportSeconds = EstimatedTeleportOverheadSeconds + EstimateMovementSeconds(postTeleportDistance);
        var estimatedSavingsSeconds = estimatedDirectSeconds - estimatedTeleportSeconds;
        if (!allowTeleport)
        {
            return new(
                FateTravelMethod.Direct,
                aetheryteId,
                aetheryteName,
                aetherytePosition,
                directDistance,
                postTeleportDistance,
                estimatedDirectSeconds,
                estimatedTeleportSeconds,
                estimatedDirectSeconds,
                estimatedSavingsSeconds,
                "teleport assistance is disabled");
        }

        if (estimatedSavingsSeconds < MinimumTeleportSavingsSeconds)
        {
            return new(
                FateTravelMethod.Direct,
                aetheryteId,
                aetheryteName,
                aetherytePosition,
                directDistance,
                postTeleportDistance,
                estimatedDirectSeconds,
                estimatedTeleportSeconds,
                estimatedDirectSeconds,
                estimatedSavingsSeconds,
                $"teleport would save only {estimatedSavingsSeconds:F1}s; requires at least {MinimumTeleportSavingsSeconds:F0}s");
        }

        return new(
            FateTravelMethod.Aetheryte,
            aetheryteId,
            aetheryteName,
            aetherytePosition,
            directDistance,
            postTeleportDistance,
            estimatedDirectSeconds,
            estimatedTeleportSeconds,
            estimatedTeleportSeconds,
            estimatedSavingsSeconds,
            $"teleport is estimated to save {estimatedSavingsSeconds:F1}s");
    }

    private static bool TryFindBestUnlockedAetheryte(
        uint territoryId,
        Vector3 targetPosition,
        out uint aetheryteId,
        out string aetheryteName,
        out Vector3 aetherytePosition,
        out float postTeleportDistance)
    {
        aetheryteId = 0;
        aetheryteName = string.Empty;
        aetherytePosition = default;
        postTeleportDistance = float.MaxValue;

        foreach (var unlocked in Svc.AetheryteList)
        {
            if (!unlocked.AetheryteData.IsValid)
                continue;

            var row = unlocked.AetheryteData.Value;
            if (!row.IsAetheryte || row.Territory.RowId != territoryId)
                continue;

            Vector3 position;
            try
            {
                position = EcMap.AetherytePosition(row);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"[ZodiacBuddy/FATE-TRAVEL] Could not resolve world position for unlocked aetheryte {unlocked.AetheryteId}; skipping it for travel planning.");
                continue;
            }

            var distance = Vector3.Distance(position, targetPosition);
            if (distance >= postTeleportDistance)
                continue;

            aetheryteId = unlocked.AetheryteId;
            var resolvedName = row.PlaceName.IsValid ? row.PlaceName.Value.Name.ExtractText() : string.Empty;
            aetheryteName = string.IsNullOrWhiteSpace(resolvedName)
                ? $"Aetheryte {unlocked.AetheryteId}"
                : resolvedName;
            aetherytePosition = position;
            postTeleportDistance = distance;
        }

        return aetheryteId != 0;
    }

    private static float EstimateMovementSeconds(float distance)
        => distance <= 0f ? 0f : distance / EstimatedTravelYalmsPerSecond;
}
