using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using Callback = ECommons.Automation.Callback;
using ZodiacBuddy.Systems.Fates;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe IBattleNpc? GetBestLeveEnemy(string? priorityTargetName = null, string? excludeName = null)
    {
        var leveDirector = GetActiveLeveDirector();
        if (leveDirector == null)
            return null;

        var player = Player.Object;
        IBattleNpc? best = null;
        var bestDistance = float.MaxValue;
        IBattleNpc? priority = null;
        var priorityDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !FateTargeting.IsAttackableEnemy(npc)
                || !BelongsToLeveDirector(npc, leveDirector))
                continue;
            if (!string.IsNullOrEmpty(excludeName) && npc.Name.TextValue.Equals(excludeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = player == null ? 0f : Vector3.Distance(player.Position, npc.Position);
            if (!string.IsNullOrEmpty(priorityTargetName) && npc.Name.TextValue.Equals(priorityTargetName, StringComparison.OrdinalIgnoreCase))
            {
                if (distance < priorityDistance)
                {
                    priority = npc;
                    priorityDistance = distance;
                }
                continue;
            }

            if (distance >= bestDistance)
                continue;
            best = npc;
            bestDistance = distance;
        }
        return priority ?? best;
    }
    private unsafe IBattleNpc? GetNearestHostileEnemyByName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var leveDirector = GetActiveLeveDirector();
        if (leveDirector == null)
            return null;

        var player = Player.Object;
        IBattleNpc? best = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || !FateTargeting.IsAttackableEnemy(npc)
                || !npc.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase)
                || !BelongsToLeveDirector(npc, leveDirector))
                continue;

            var distance = player == null ? 0f : Vector3.Distance(player.Position, npc.Position);
            if (distance >= bestDistance)
                continue;
            best = npc;
            bestDistance = distance;
        }
        return best;
    }
    private unsafe IGameObject? FindNearestNamedObject(string? name, bool targetableOnly)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var leveDirector = GetActiveLeveDirector();
        if (leveDirector == null)
            return null;

        var player = Player.Object;
        IGameObject? best = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj.Address == nint.Zero
                || targetableOnly && !obj.IsTargetable
                || !obj.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase)
                || !BelongsToLeveDirector(obj, leveDirector))
                continue;

            var distance = player == null ? 0f : Vector3.Distance(player.Position, obj.Position);
            if (distance >= bestDistance)
                continue;
            best = obj;
            bestDistance = distance;
        }
        return best;
    }
    private unsafe Director* GetActiveLeveDirector()
        => LeveDirectorOwnership.GetActive(_leveContext.AutomationTarget.LeveId);

    private static unsafe bool BelongsToLeveDirector(IGameObject obj, Director* director)
        => LeveDirectorOwnership.Belongs(obj, director);

    private static unsafe int GetLeveKeyItemCount(uint itemId)
    {
        if (itemId == 0)
            return 0;
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return 0;
        var container = inventory->GetInventoryContainer(InventoryType.KeyItems);
        if (container == null)
            return 0;

        var count = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId == itemId)
                count += (int)slot->Quantity;
        }
        return count;
    }
    private unsafe IBattleNpc? GetBestLeveDefenseThreat(IGameObject? charge)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        var leveDirector = GetActiveLeveDirector();
        var playerId = player.GameObjectId;
        var chargeId = charge?.GameObjectId ?? 0;
        var leashSquared = LeveDefendThreatLeashDistance * LeveDefendThreatLeashDistance;
        IBattleNpc? best = null;
        var bestPriority = int.MaxValue;
        var bestDistance = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || !FateTargeting.IsAttackableEnemy(npc))
                continue;

            var targetsCharge = chargeId != 0 && npc.TargetObjectId == chargeId;
            var targetsPlayer = npc.TargetObjectId == playerId;
            var directorObjective = leveDirector != null && BelongsToLeveDirector(npc, leveDirector);
            if (!targetsCharge && !targetsPlayer && !directorObjective)
                continue;

            var playerDistance = Vector3.DistanceSquared(player.Position, npc.Position);
            var chargeDistance = charge == null ? float.MaxValue : Vector3.DistanceSquared(charge.Position, npc.Position);
            if (playerDistance > leashSquared && chargeDistance > leashSquared)
                continue;

            var priority = targetsCharge ? 0 : targetsPlayer ? 1 : 2;
            var distance = Math.Min(playerDistance, chargeDistance);
            if (priority > bestPriority || priority == bestPriority && distance >= bestDistance)
                continue;

            best = npc;
            bestPriority = priority;
            bestDistance = distance;
        }
        return best;
    }
}
