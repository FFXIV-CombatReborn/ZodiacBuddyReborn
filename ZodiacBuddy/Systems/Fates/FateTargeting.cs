using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using System;
using System.Collections.Generic;
using System.Numerics;
using ZodiacBuddy;

namespace ZodiacBuddy.Systems.Fates;

internal readonly record struct FateDefendThreatSelection(
    IBattleNpc? Target,
    IGameObject? ProtectedObject,
    int ProtectedAttackerCount,
    int TotalObjectiveThreatCount);

internal static unsafe class FateTargeting
{
    private const float MaxStarterDistanceFromStaging = 160f;
    private const float DirectThreatFateLeashPadding = 12f;
    private const uint InvalidMotivationNpcEntityId = 0xE0000000;

    internal static ushort GetFateId(IGameObject obj)
        => obj.Address == nint.Zero ? (ushort)0 : ((FFXIVGameObject*)obj.Address)->FateId;

    internal static uint GetNameplateIconId(IGameObject obj)
        => obj.Address == nint.Zero ? 0u : ((FFXIVGameObject*)obj.Address)->NamePlateIconId;

    internal static bool IsHostileEnemy(IBattleNpc npc)
    {
        try
        {
            return ObjectFunctions.IsHostile(npc);
        }
        catch
        {
            return npc.BattleNpcKind == BattleNpcSubKind.Combatant;
        }
    }

    internal static bool IsAttackableEnemy(IBattleNpc npc)
        => npc.Address != nint.Zero
            && !npc.IsDead
            && npc.CurrentHp != 0
            && npc.IsTargetable
            && IsHostileEnemy(npc);

    internal static bool IsFateEnemy(IGameObject obj, ushort fateId)
        => fateId != 0
            && obj is IBattleNpc npc
            && GetFateId(obj) == fateId
            && IsAttackableEnemy(npc);

    internal static bool IsFriendlyFateNpc(IBattleNpc npc, ushort fateId)
        => fateId != 0
            && !npc.IsDead
            && npc.MaxHp > 0
            && GetFateId(npc) == fateId
            && !IsAttackableEnemy(npc);

    internal static bool TryGetFriendlyFateNpc(ulong gameObjectId, ushort fateId, out IBattleNpc npc)
    {
        if (gameObjectId != 0
            && TryGetObject(gameObjectId, out var obj)
            && obj is IBattleNpc candidate
            && IsFriendlyFateNpc(candidate, fateId))
        {
            npc = candidate;
            return true;
        }

        npc = default!;
        return false;
    }

    internal static void TrackVisibleFateEnemyIds(ushort fateId, HashSet<ulong> knownFateEnemyIds)
    {
        if (fateId == 0)
            return;

        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleNpc npc
                && GetFateId(npc) == fateId
                && IsAttackableEnemy(npc))
                knownFateEnemyIds.Add(npc.GameObjectId);
        }
    }

    internal static int CountKnownEnemiesStillTagged(ushort fateId, HashSet<ulong> knownFateEnemyIds)
    {
        if (fateId == 0 || knownFateEnemyIds.Count == 0)
            return 0;

        var count = 0;
        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleNpc npc
                && knownFateEnemyIds.Contains(npc.GameObjectId)
                && GetFateId(npc) == fateId)
                count++;
        }
        return count;
    }

    internal static IBattleNpc? GetBestResidualAggroTarget(
        ushort endedFateId,
        HashSet<ulong> knownFateEnemyIds,
        ulong stickyTargetId,
        Vector3 cleanupOrigin,
        float maxPursuitDistance,
        float acquireDistance,
        out bool allowPursuit,
        out int ignoredFormerFateEnemies)
    {
        allowPursuit = false;
        ignoredFormerFateEnemies = 0;
        var player = Player.Object;
        if (player == null)
            return null;

        var protectedActorIds = BuildProtectedActorIds(out _);
        var maxPursuitDistanceSquared = maxPursuitDistance * maxPursuitDistance;
        var acquireDistanceSquared = acquireDistance * acquireDistance;
        IBattleNpc? nearestOrdinary = null;
        IBattleNpc? nearestFormerFate = null;
        var nearestOrdinaryDistance = float.MaxValue;
        var nearestFormerFateDistance = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || !IsAttackableEnemy(npc))
                continue;

            var currentFateId = GetFateId(npc);
            var formerFateEnemy = knownFateEnemyIds.Contains(npc.GameObjectId)
                || (endedFateId != 0 && currentFateId == endedFateId);
            var targetingProtectedActor = npc.TargetObjectId != 0 && protectedActorIds.Contains(npc.TargetObjectId);
            if (!targetingProtectedActor)
            {
                if (formerFateEnemy)
                    ignoredFormerFateEnemies++;
                continue;
            }

            var distanceFromPlayer = Vector3.DistanceSquared(player.Position, npc.Position);
            var distanceFromOrigin = Vector3.DistanceSquared(cleanupOrigin, npc.Position);
            if (distanceFromOrigin > maxPursuitDistanceSquared)
            {
                if (formerFateEnemy)
                    ignoredFormerFateEnemies++;
                continue;
            }

            if (npc.GameObjectId == stickyTargetId)
            {
                allowPursuit = true;
                return npc;
            }

            if (distanceFromPlayer > acquireDistanceSquared)
            {
                if (formerFateEnemy)
                    ignoredFormerFateEnemies++;
                continue;
            }

            if (formerFateEnemy)
            {
                if (distanceFromPlayer < nearestFormerFateDistance)
                {
                    nearestFormerFateDistance = distanceFromPlayer;
                    nearestFormerFate = npc;
                }
                continue;
            }

            if (distanceFromPlayer < nearestOrdinaryDistance)
            {
                nearestOrdinaryDistance = distanceFromPlayer;
                nearestOrdinary = npc;
            }
        }

        if (nearestOrdinary != null)
        {
            allowPursuit = true;
            return nearestOrdinary;
        }

        if (nearestFormerFate != null)
        {
            allowPursuit = true;
            return nearestFormerFate;
        }

        return null;
    }

    internal static HashSet<ulong> GetCurrentFateThreatIds(ushort activeFateId)
    {
        var result = new HashSet<ulong>();
        if (activeFateId == 0)
            return result;

        var protectedActorIds = BuildProtectedActorIds(out _);
        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleNpc npc
                && IsAttackableEnemy(npc)
                && GetFateId(npc) == activeFateId
                && npc.TargetObjectId != 0
                && protectedActorIds.Contains(npc.TargetObjectId))
                result.Add(npc.GameObjectId);
        }

        return result;
    }

    internal static IBattleNpc? GetBestAggroTarget(ulong stickyTargetId)
        => GetBestDirectThreatTarget(stickyTargetId, out _, out _, out _);

    internal static IBattleNpc? GetBestAggroTarget(ushort activeFateId, ulong stickyTargetId, Vector3 fateCenter, float fateRadius)
        => GetBestDirectThreatTarget(activeFateId, stickyTargetId, fateCenter, fateRadius, out _, out _, out _);

    internal static IBattleNpc? GetBestDirectThreatTarget(ulong stickyTargetId, out int directThreatCount, out int buddyThreatCount, out bool stickyTargetValid)
        => GetBestDirectThreatTargetCore(0, stickyTargetId, null, 0f, false, false, out directThreatCount, out buddyThreatCount, out _, out stickyTargetValid);

    internal static IBattleNpc? GetBestDirectThreatTarget(
        ushort activeFateId,
        ulong stickyTargetId,
        Vector3 fateCenter,
        float fateRadius,
        out int directThreatCount,
        out int buddyThreatCount,
        out bool stickyTargetValid)
        => GetBestDirectThreatTarget(activeFateId, stickyTargetId, fateCenter, fateRadius, false, out directThreatCount, out buddyThreatCount, out _, out stickyTargetValid);

    internal static IBattleNpc? GetBestDirectThreatTarget(
        ushort activeFateId,
        ulong stickyTargetId,
        Vector3 fateCenter,
        float fateRadius,
        bool excludeActiveFateThreats,
        out int directThreatCount,
        out int buddyThreatCount,
        out int ignoredActiveFateThreatCount,
        out bool stickyTargetValid)
        => GetBestDirectThreatTargetCore(activeFateId, stickyTargetId, fateCenter, fateRadius, true, excludeActiveFateThreats, out directThreatCount, out buddyThreatCount, out ignoredActiveFateThreatCount, out stickyTargetValid);

    private static IBattleNpc? GetBestDirectThreatTargetCore(
        ushort activeFateId,
        ulong stickyTargetId,
        Vector3? fateCenter,
        float fateRadius,
        bool stickyWins,
        bool excludeActiveFateThreats,
        out int directThreatCount,
        out int buddyThreatCount,
        out int ignoredActiveFateThreatCount,
        out bool stickyTargetValid)
    {
        directThreatCount = 0;
        buddyThreatCount = 0;
        ignoredActiveFateThreatCount = 0;
        stickyTargetValid = false;
        var player = Player.Object;
        if (player == null)
            return null;

        var protectedActorIds = BuildProtectedActorIds(out var playerIds);

        IBattleNpc? sticky = null;
        IBattleNpc? lowestHp = null;
        var lowestCurrentHp = uint.MaxValue;
        var lowestHpDistance = float.MaxValue;
        var lowestIsSticky = false;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !IsAttackableEnemy(npc)
                || npc.TargetObjectId == 0
                || !protectedActorIds.Contains(npc.TargetObjectId)
                || !IsDirectThreatInScope(npc, activeFateId, fateCenter, fateRadius))
                continue;

            if (excludeActiveFateThreats && activeFateId != 0 && GetFateId(npc) == activeFateId)
            {
                ignoredActiveFateThreatCount++;
                continue;
            }

            directThreatCount++;
            if (!playerIds.Contains(npc.TargetObjectId))
                buddyThreatCount++;

            var isSticky = npc.GameObjectId == stickyTargetId;
            if (isSticky)
            {
                stickyTargetValid = true;
                if (stickyWins)
                {
                    sticky = npc;
                    continue;
                }
            }

            var currentHp = npc.CurrentHp;
            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (lowestHp == null
                || currentHp < lowestCurrentHp
                || (currentHp == lowestCurrentHp && isSticky && !lowestIsSticky)
                || (currentHp == lowestCurrentHp && isSticky == lowestIsSticky && distance < lowestHpDistance))
            {
                lowestHp = npc;
                lowestCurrentHp = currentHp;
                lowestHpDistance = distance;
                lowestIsSticky = isSticky;
            }
        }

        return sticky ?? lowestHp;
    }

    private static bool IsDirectThreatInScope(IBattleNpc npc, ushort activeFateId, Vector3? fateCenter, float fateRadius)
    {
        if (activeFateId == 0 || !fateCenter.HasValue)
            return true;

        if (GetFateId(npc) == activeFateId)
            return true;

        var leash = MathF.Max(0f, fateRadius) + DirectThreatFateLeashPadding;
        var dx = npc.Position.X - fateCenter.Value.X;
        var dz = npc.Position.Z - fateCenter.Value.Z;
        return dx * dx + dz * dz <= leash * leash;
    }

    private static HashSet<ulong> BuildProtectedActorIds(out HashSet<ulong> playerIds)
    {
        playerIds = new HashSet<ulong>();
        var player = Player.Object;
        if (player == null)
            return new HashSet<ulong>();

        AddActorIdentity(playerIds, player.GameObjectId, player.EntityId);
        var protectedActorIds = new HashSet<ulong>(playerIds);

        var companion = Service.BuddyList.CompanionBuddy;
        if (companion != null)
            AddActorIdentity(protectedActorIds, companion.GameObject?.GameObjectId ?? 0, companion.EntityId);

        var pet = Service.BuddyList.PetBuddy;
        if (pet != null)
            AddActorIdentity(protectedActorIds, pet.GameObject?.GameObjectId ?? 0, pet.EntityId);

        foreach (var obj in Svc.Objects)
        {
            if (obj.GameObjectId != 0 && obj.OwnerId == player.EntityId)
                AddActorIdentity(protectedActorIds, obj.GameObjectId, obj.EntityId);
        }

        return protectedActorIds;
    }

    private static void AddActorIdentity(HashSet<ulong> actorIds, ulong gameObjectId, uint entityId)
    {
        if (gameObjectId != 0)
            actorIds.Add(gameObjectId);
        if (entityId != 0 && entityId != InvalidMotivationNpcEntityId)
            actorIds.Add(entityId);
    }

    internal static IBattleNpc? GetBestIncidentalAggroTarget(ushort activeFateId, ulong stickyTargetId, Vector3 leashCenter, float leashDistance)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        var protectedActorIds = BuildProtectedActorIds(out _);
        var leashDistanceSquared = leashDistance * leashDistance;
        IBattleNpc? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !IsAttackableEnemy(npc)
                || npc.TargetObjectId == 0
                || !protectedActorIds.Contains(npc.TargetObjectId)
                || GetFateId(npc) == activeFateId
                || Vector3.DistanceSquared(leashCenter, npc.Position) > leashDistanceSquared)
                continue;

            if (npc.GameObjectId == stickyTargetId)
                return npc;

            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = npc;
            }
        }

        return nearest;
    }

    internal static IBattleNpc? GetBestCombatTargetByName(ushort fateId, ulong stickyTargetId, string targetName)
    {
        if (stickyTargetId != 0)
        {
            foreach (var obj in Svc.Objects)
            {
                if (obj is IBattleNpc sticky
                    && sticky.GameObjectId == stickyTargetId
                    && IsFateEnemy(sticky, fateId)
                    && sticky.Name.TextValue.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    return sticky;
            }
        }

        var player = Player.Object;
        if (player == null)
            return null;

        IBattleNpc? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !IsFateEnemy(npc, fateId)
                || !npc.Name.TextValue.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = npc;
            }
        }

        return nearest;
    }

    internal static IBattleNpc? GetNearestCombatTarget(ushort fateId, ulong stickyTargetId)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        IBattleNpc? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || !IsFateEnemy(npc, fateId))
                continue;

            if (npc.GameObjectId == stickyTargetId)
                return npc;

            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = npc;
            }
        }

        return nearest;
    }

    internal static FateDefendThreatSelection GetBestDefendObjectiveThreat(ushort fateId, ulong stickyTargetId, ulong stickyProtectedObjectId, float objectiveSwitchHpMargin)
    {
        var player = Player.Object;
        if (player == null || fateId == 0)
            return default;

        var protectedByIdentity = new Dictionary<ulong, IGameObject>();
        var protectedByGameObjectId = new Dictionary<ulong, IGameObject>();
        foreach (var obj in Svc.Objects)
        {
            if (!IsDefendProtectedObject(obj, fateId))
                continue;

            if (obj.GameObjectId != 0)
            {
                protectedByIdentity[obj.GameObjectId] = obj;
                protectedByGameObjectId[obj.GameObjectId] = obj;
            }
            if (obj.EntityId != 0 && obj.EntityId != InvalidMotivationNpcEntityId)
                protectedByIdentity[obj.EntityId] = obj;
        }

        if (protectedByGameObjectId.Count == 0)
            return default;

        var attackersByProtectedObject = new Dictionary<ulong, List<IBattleNpc>>();
        var totalObjectiveThreatCount = 0;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc enemy
                || !IsFateEnemy(enemy, fateId)
                || enemy.TargetObjectId == 0
                || !protectedByIdentity.TryGetValue(enemy.TargetObjectId, out var protectedObject)
                || protectedObject.GameObjectId == 0)
                continue;

            if (!attackersByProtectedObject.TryGetValue(protectedObject.GameObjectId, out var attackers))
            {
                attackers = [];
                attackersByProtectedObject[protectedObject.GameObjectId] = attackers;
            }
            attackers.Add(enemy);
            totalObjectiveThreatCount++;
        }

        if (attackersByProtectedObject.Count == 0)
            return default;

        IGameObject? bestProtectedObject = null;
        var bestProtectedAttackers = 0;
        var bestProtectedHealth = float.MaxValue;
        var bestProtectedDistance = float.MaxValue;
        foreach (var pair in attackersByProtectedObject)
        {
            if (!protectedByGameObjectId.TryGetValue(pair.Key, out var protectedObject))
                continue;

            var health = GetDefendProtectedHealthRatio(protectedObject);
            var distance = Vector3.DistanceSquared(player.Position, protectedObject.Position);
            var isSticky = protectedObject.GameObjectId == stickyProtectedObjectId;
            var bestIsSticky = bestProtectedObject?.GameObjectId == stickyProtectedObjectId;
            if (bestProtectedObject == null
                || health < bestProtectedHealth
                || (MathF.Abs(health - bestProtectedHealth) < 0.0001f && pair.Value.Count > bestProtectedAttackers)
                || (MathF.Abs(health - bestProtectedHealth) < 0.0001f && pair.Value.Count == bestProtectedAttackers && isSticky && !bestIsSticky)
                || (MathF.Abs(health - bestProtectedHealth) < 0.0001f && pair.Value.Count == bestProtectedAttackers && isSticky == bestIsSticky && distance < bestProtectedDistance))
            {
                bestProtectedObject = protectedObject;
                bestProtectedAttackers = pair.Value.Count;
                bestProtectedHealth = health;
                bestProtectedDistance = distance;
            }
        }

        if (bestProtectedObject == null)
            return default;

        if (stickyProtectedObjectId != 0
            && bestProtectedObject.GameObjectId != stickyProtectedObjectId
            && protectedByGameObjectId.TryGetValue(stickyProtectedObjectId, out var stickyProtectedObject)
            && attackersByProtectedObject.TryGetValue(stickyProtectedObjectId, out var stickyProtectedAttackers))
        {
            var stickyHealth = GetDefendProtectedHealthRatio(stickyProtectedObject);
            var attackerAdvantage = bestProtectedAttackers - stickyProtectedAttackers.Count;
            if (stickyHealth <= bestProtectedHealth + MathF.Max(0f, objectiveSwitchHpMargin) && attackerAdvantage < 3)
            {
                bestProtectedObject = stickyProtectedObject;
                bestProtectedAttackers = stickyProtectedAttackers.Count;
            }
        }

        if (!attackersByProtectedObject.TryGetValue(bestProtectedObject.GameObjectId, out var selectedAttackers)
            || selectedAttackers.Count == 0)
            return default;

        IBattleNpc? selectedTarget = null;
        var selectedTargetHp = uint.MaxValue;
        var selectedTargetDistance = float.MaxValue;
        foreach (var attacker in selectedAttackers)
        {
            if (attacker.GameObjectId == stickyTargetId)
            {
                selectedTarget = attacker;
                break;
            }

            var distance = Vector3.DistanceSquared(player.Position, attacker.Position);
            if (selectedTarget == null
                || attacker.CurrentHp < selectedTargetHp
                || (attacker.CurrentHp == selectedTargetHp && distance < selectedTargetDistance))
            {
                selectedTarget = attacker;
                selectedTargetHp = attacker.CurrentHp;
                selectedTargetDistance = distance;
            }
        }

        return new(selectedTarget, bestProtectedObject, bestProtectedAttackers, totalObjectiveThreatCount);
    }

    private static bool IsDefendProtectedObject(IGameObject obj, ushort fateId)
    {
        if (obj.Address == nint.Zero
            || GetFateId(obj) != fateId
            || obj.OwnerId != 0
            || obj.ObjectKind != ObjectKind.BattleNpc && obj.ObjectKind != ObjectKind.EventObj)
            return false;
        if (obj is IBattleNpc battleNpc)
            return !battleNpc.IsDead && battleNpc.CurrentHp != 0 && battleNpc.MaxHp > 0 && !IsAttackableEnemy(battleNpc);
        return true;
    }

    private static float GetDefendProtectedHealthRatio(IGameObject obj)
        => obj is IBattleNpc battleNpc && battleNpc.MaxHp > 0
            ? Math.Clamp((float)battleNpc.CurrentHp / battleNpc.MaxHp, 0f, 1f)
            : 1f;

    internal static IBattleNpc? GetBestCombatTarget(ushort fateId, ulong stickyTargetId, Vector3? leashCenter = null, float leashDistance = float.MaxValue, ulong additionalDefendedObjectId = 0)
    {
        var leashDistanceSquared = leashDistance * leashDistance;
        var defendedIds = new HashSet<ulong>();
        if (additionalDefendedObjectId != 0)
        {
            defendedIds.Add(additionalDefendedObjectId);
        }
        else
        {
            foreach (var obj in Svc.Objects)
            {
                if (obj is not IBattleNpc friendly
                    || friendly.IsDead
                    || friendly.MaxHp == 0
                    || GetFateId(friendly) != fateId
                    || IsAttackableEnemy(friendly))
                    continue;

                defendedIds.Add(friendly.GameObjectId);
            }
        }

        var player = Player.Object;
        if (player == null)
            return null;

        IBattleNpc? highestHpThreat = null;
        IBattleNpc? highestHp = null;
        uint highestThreatMaxHp = 0;
        uint highestMaxHp = 0;
        var highestThreatDistance = float.MaxValue;
        var highestDistance = float.MaxValue;
        var highestThreatIsSticky = false;
        var highestIsSticky = false;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !IsFateEnemy(npc, fateId)
                || (leashCenter.HasValue && Vector3.DistanceSquared(leashCenter.Value, npc.Position) > leashDistanceSquared))
                continue;

            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            var isSticky = npc.GameObjectId == stickyTargetId;
            var maxHp = npc.MaxHp;
            if (defendedIds.Count > 0 && npc.TargetObjectId != 0 && defendedIds.Contains(npc.TargetObjectId))
            {
                if (highestHpThreat == null
                    || maxHp > highestThreatMaxHp
                    || (maxHp == highestThreatMaxHp && isSticky && !highestThreatIsSticky)
                    || (maxHp == highestThreatMaxHp && isSticky == highestThreatIsSticky && distance < highestThreatDistance))
                {
                    highestHpThreat = npc;
                    highestThreatMaxHp = maxHp;
                    highestThreatDistance = distance;
                    highestThreatIsSticky = isSticky;
                }
            }

            if (highestHp == null
                || maxHp > highestMaxHp
                || (maxHp == highestMaxHp && isSticky && !highestIsSticky)
                || (maxHp == highestMaxHp && isSticky == highestIsSticky && distance < highestDistance))
            {
                highestHp = npc;
                highestMaxHp = maxHp;
                highestDistance = distance;
                highestIsSticky = isSticky;
            }
        }

        return leashCenter.HasValue ? highestHpThreat ?? highestHp : highestHp;
    }

    internal static IBattleNpc? GetFriendlyFateNpcByName(ushort fateId, string expectedName)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        IBattleNpc? best = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || npc.IsDead
                || npc.MaxHp == 0
                || GetFateId(npc) != fateId
                || IsAttackableEnemy(npc)
                || !npc.Name.TextValue.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = npc;
            }
        }

        return best;
    }

    internal static IBattleNpc? GetMostTargetedFriendlyFateNpc(ushort fateId)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        var attackersByTarget = new Dictionary<ulong, int>();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc enemy
                || !IsFateEnemy(enemy, fateId)
                || enemy.TargetObjectId == 0)
                continue;

            attackersByTarget.TryGetValue(enemy.TargetObjectId, out var count);
            attackersByTarget[enemy.TargetObjectId] = count + 1;
        }

        IBattleNpc? best = null;
        var bestAttackers = 0;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !IsFriendlyFateNpc(npc, fateId)
                || !attackersByTarget.TryGetValue(npc.GameObjectId, out var attackerCount)
                || attackerCount == 0)
                continue;

            var distance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (best == null
                || attackerCount > bestAttackers
                || (attackerCount == bestAttackers && distance < bestDistance))
            {
                best = npc;
                bestAttackers = attackerCount;
                bestDistance = distance;
            }
        }

        return best;
    }

    internal static IGameObject? GetMostTargetedFateObject(ushort fateId)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        var attackersByTarget = new Dictionary<ulong, int>();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc enemy
                || !IsFateEnemy(enemy, fateId)
                || enemy.TargetObjectId == 0)
                continue;

            attackersByTarget.TryGetValue(enemy.TargetObjectId, out var count);
            attackersByTarget[enemy.TargetObjectId] = count + 1;
        }

        IGameObject? best = null;
        var bestAttackers = 0;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (GetFateId(obj) != fateId
                || !attackersByTarget.TryGetValue(obj.GameObjectId, out var attackerCount)
                || attackerCount == 0
                || obj is IBattleNpc battleNpc && IsAttackableEnemy(battleNpc))
                continue;

            var distance = Vector3.DistanceSquared(player.Position, obj.Position);
            if (best == null
                || attackerCount > bestAttackers
                || (attackerCount == bestAttackers && distance < bestDistance))
            {
                best = obj;
                bestAttackers = attackerCount;
                bestDistance = distance;
            }
        }

        return best;
    }

    internal static IGameObject? GetNearestEventObject(ushort fateId)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        IGameObject? best = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj
                || !obj.IsTargetable
                || GetFateId(obj) != fateId)
                continue;

            var distance = Vector3.DistanceSquared(player.Position, obj.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = obj;
            }
        }

        return best;
    }

    internal static IGameObject? FindStartNpc(ushort fateId, Vector3 stagingPosition, string? fallbackName = null)
    {
        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        var motivationNpc = context == null ? 0u : context->MotivationNpc;
        var motivationNpcUsable = motivationNpc != 0 && motivationNpc != InvalidMotivationNpcEntityId;

        IGameObject? named = null;
        IGameObject? namedWithIcon = null;
        IGameObject? exact = null;
        IGameObject? nearbyIcon = null;
        var namedDistance = float.MaxValue;
        var namedWithIconDistance = float.MaxValue;
        var exactDistance = float.MaxValue;
        var iconDistance = float.MaxValue;

        var player = Player.Object;
        if (player == null)
            return null;

        foreach (var obj in Svc.Objects)
        {
            if (!obj.IsTargetable)
                continue;
            if (obj is IBattleNpc battleNpc && IsAttackableEnemy(battleNpc))
                continue;

            var playerDistance = Vector3.DistanceSquared(player.Position, obj.Position);
            var stagingDistance = Vector3.DistanceSquared(stagingPosition, obj.Position);
            var withinStarterArea = stagingDistance <= MaxStarterDistanceFromStaging * MaxStarterDistanceFromStaging;

            if (withinStarterArea
                && motivationNpcUsable
                && obj.EntityId == motivationNpc
                && playerDistance < exactDistance)
            {
                exactDistance = playerDistance;
                exact = obj;
            }

            if (withinStarterArea
                && !string.IsNullOrWhiteSpace(fallbackName)
                && obj.Name.TextValue.Equals(fallbackName, StringComparison.OrdinalIgnoreCase))
            {
                if (playerDistance < namedDistance)
                {
                    namedDistance = playerDistance;
                    named = obj;
                }

                if (GetNameplateIconId(obj) != 0 && playerDistance < namedWithIconDistance)
                {
                    namedWithIconDistance = playerDistance;
                    namedWithIcon = obj;
                }
            }

            if (GetNameplateIconId(obj) == 0)
                continue;
            if (stagingDistance > 20f * 20f)
                continue;
            if (playerDistance < iconDistance)
            {
                iconDistance = playerDistance;
                nearbyIcon = obj;
            }
        }

        return string.IsNullOrWhiteSpace(fallbackName)
            ? exact ?? nearbyIcon
            : namedWithIcon ?? named ?? exact;
    }

    internal static void LogStartNpcCandidates(ushort fateId, Vector3 stagingPosition, string fallbackName)
    {
        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        var motivationNpc = context == null ? 0u : context->MotivationNpc;
        var motivationNpcUsable = motivationNpc != 0 && motivationNpc != InvalidMotivationNpcEntityId;
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Starter diagnostic FateId={fateId} motivationNpc={motivationNpc} motivationUsable={motivationNpcUsable} fallback='{fallbackName}' staging={stagingPosition}.");

        foreach (var obj in Svc.Objects)
        {
            var name = obj.Name.TextValue;
            var nameMatch = name.Equals(fallbackName, StringComparison.OrdinalIgnoreCase);
            var motivationMatch = motivationNpcUsable && obj.EntityId == motivationNpc;
            var stagingDistance = Vector3.DistanceSquared(stagingPosition, obj.Position);
            if (!nameMatch && !motivationMatch && stagingDistance > 30f * 30f)
                continue;

            Service.PluginLog.Verbose(
                $"[ZodiacBuddy/FATE] Starter candidate FateId={fateId} name='{name}' kind={obj.ObjectKind} targetable={obj.IsTargetable} " +
                $"entityId={obj.EntityId} gameObjectId={obj.GameObjectId} icon={GetNameplateIconId(obj)} objectFateId={GetFateId(obj)} " +
                $"position={obj.Position} stagingDistance={MathF.Sqrt(stagingDistance):F1} withinStarterArea={stagingDistance <= MaxStarterDistanceFromStaging * MaxStarterDistanceFromStaging} " +
                $"nameMatch={nameMatch} motivationMatch={motivationMatch}.");
        }
    }

    internal static IGameObject? FindByEntityId(uint entityId)
    {
        if (entityId == 0 || entityId == InvalidMotivationNpcEntityId)
            return null;

        foreach (var obj in Svc.Objects)
        {
            if (obj.EntityId == entityId)
                return obj;
        }

        return null;
    }

    internal static bool TryGetObject(ulong gameObjectId, out IGameObject obj)
    {
        foreach (var candidate in Svc.Objects)
        {
            if (candidate.GameObjectId == gameObjectId)
            {
                obj = candidate;
                return true;
            }
        }

        obj = default!;
        return false;
    }
}
