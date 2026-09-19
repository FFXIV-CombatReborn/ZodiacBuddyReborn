using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using System;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe void DriveEscortFateCombat(IFate fate)
    {
        var anchor = ResolveFateProtectedAnchor(fate.FateId, _fateContext.EscortAnchorId, out var source);
        if (anchor != null && _fateContext.EscortAnchorId != anchor.GameObjectId)
        {
            _fateContext.EscortAnchorId = anchor.GameObjectId;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Escort anchor resolved from {source}: '{anchor.Name.TextValue}' id={anchor.GameObjectId} for FateId={fate.FateId}.");
        }

        if (anchor == null)
        {
            ReleaseFateTacticalMovement();
            _fateCombatEngagement.Reset();
            _fateContext.CombatTargetId = 0;
            _fateContext.IncidentalAggroTargetId = 0;
            StopFateOwnedNavigation();
            var fallbackThreat = _fateCombatTargets.SelectFallbackDirectThreat(fate.FateId, fate.Position, fate.Radius);
            if (fallbackThreat != null)
            {
                DriveFateEnemy(fate, fallbackThreat);
                return;
            }
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop("escort anchor is unavailable and no scoped combat target is eligible");
            _fateContext.AutomationStatus = $"Waiting to resolve the protected escort actor in {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        var player = Player.Object!;
        var anchorDistanceSquared = NavigationGeometry.HorizontalDistanceSquared(player.Position, anchor.Position);
        if (anchorDistanceSquared > FateEscortHardLeashDistance * FateEscortHardLeashDistance)
        {
            ReleaseFateTacticalMovement();
            _fateCombatEngagement.Reset();
            _fateContext.CombatTargetId = 0;
            _fateContext.IncidentalAggroTargetId = 0;
            if (DateTime.Now >= _fateContext.EscortFollowRestartAt)
                FollowFateEscort(fate, anchor, true);
            else
                _fateContext.AutomationStatus = $"Reassessing route back to {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        var target = FateTargeting.GetBestCombatTarget(fate.FateId, _fateContext.CombatTargetId, anchor.Position, FateEscortCombatLeashDistance, anchor.GameObjectId);
        if (target != null)
        {
            _fateContext.IncidentalAggroTargetId = 0;
            DriveFateEnemy(fate, target);
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            var incidentalTarget = FateTargeting.GetBestIncidentalAggroTarget(
                fate.FateId,
                _fateContext.IncidentalAggroTargetId,
                anchor.Position,
                FateEscortIncidentalAggroLeashDistance);
            if (incidentalTarget != null)
            {
                if (_fateContext.IncidentalAggroTargetId != incidentalTarget.GameObjectId)
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Incidental aggro target '{incidentalTarget.Name.TextValue}' id={incidentalTarget.GameObjectId} fateId={FateTargeting.GetFateId(incidentalTarget)} acquired near escort anchor {anchor.GameObjectId}.");
                _fateContext.IncidentalAggroTargetId = incidentalTarget.GameObjectId;
                DriveFateEnemy(fate, incidentalTarget, FateNavigationIntent.IncidentalAggro, "incidental attacker");
                return;
            }
        }

        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        _fateCombatEngagement.Reset();
        ReleaseFateTacticalMovement();
        if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
            RequestFateRotationSolverStop("escort has no committed combat target");
        if (anchorDistanceSquared > FateEscortFollowStartDistance * FateEscortFollowStartDistance)
        {
            if (DateTime.Now >= _fateContext.EscortFollowRestartAt)
                FollowFateEscort(fate, anchor, false);
            else
                _fateContext.AutomationStatus = $"Holding for escort follow cadence near {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        StopFateOwnedNavigation();
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            _fateContext.AutomationStatus = $"Holding near {anchor.Name.TextValue}; combat is active but no local escort-safe threat is eligible for target ownership in {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        _fateContext.AutomationStatus = $"Holding near {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)}.";
    }

    private bool TryRefreshEscortObstacleMap(IFate fate)
    {
        var anchor = ResolveFateProtectedAnchor(fate.FateId, _fateContext.EscortAnchorId, out var source);
        if (anchor == null)
            return false;

        if (_fateContext.EscortAnchorId != anchor.GameObjectId)
        {
            _fateContext.EscortAnchorId = anchor.GameObjectId;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Escort anchor resolved from {source}: '{anchor.Name.TextValue}' id={anchor.GameObjectId} for FateId={fate.FateId}.");
        }

        if (!_fateObstacleMaps.NeedsEscortRefresh(anchor.Position, FateEscortObstacleMapRecenterDistance))
            return false;

        ReleaseFateRotationSolverNow("the moving escort requires a fresh local BossMod obstacle map");
        ReleaseFateTacticalMovement();
        _fateCombatEngagement.Reset();
        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        StopFateOwnedNavigation();

        if (!_fateObstacleMaps.BeginEscortRefresh(
                fate.FateId,
                FateMetadata.GetName(fate.FateId),
                Service.ClientState.TerritoryType,
                anchor.Position,
                FateEscortObstacleMapLocalRadius))
            return false;

        _fateContext.AutomationStatus = _fateObstacleMaps.Status;
        return true;
    }

    private void FollowFateEscort(IFate fate, IBattleNpc anchor, bool hardLeash)
    {
        var destination = ResolveReachableProtectedAnchorDestination(anchor.Position) ?? anchor.Position;
        EnsureFateNavigation(destination, false, $"escort anchor {fate.FateId}", FateNavigationIntent.EscortAnchor);
        var distance = MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, anchor.Position));
        _fateContext.AutomationStatus = hardLeash
            ? $"Returning to {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)} ({distance:F1}y)."
            : $"Following {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)} ({distance:F1}y).";
    }

    private void PinFateEscortAnchor(ushort fateId, IGameObject npc)
    {
        if (npc is not IBattleNpc battleNpc
            || battleNpc.IsDead
            || battleNpc.MaxHp == 0
            || FateTargeting.IsAttackableEnemy(battleNpc))
            return;

        if (_fateContext.EscortAnchorId == npc.GameObjectId)
            return;

        _fateContext.EscortAnchorId = npc.GameObjectId;
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Pinned preparing Escort starter/protected actor '{npc.Name.TextValue}' id={npc.GameObjectId} for FateId={fateId}.");
    }

    private unsafe IBattleNpc? ResolveFateProtectedAnchor(ushort fateId, ulong pinnedId, out string source)
    {
        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        if (context != null)
        {
            var objective = ResolveRuntimeFriendlyFateActor(context->ObjectiveNpc);
            if (objective != null)
            {
                source = "ObjectiveNpc";
                return objective;
            }
        }

        var fallbackName = FateMetadata.GetEscortNpcName(fateId);
        if (fallbackName != null)
        {
            var named = FateTargeting.GetFriendlyFateNpcByName(fateId, fallbackName);
            if (named != null)
            {
                source = "known escort-name fallback";
                return named;
            }
        }

        var defended = FateTargeting.GetMostTargetedFriendlyFateNpc(fateId);
        if (defended != null)
        {
            source = "friendly actor currently targeted by FATE enemies";
            return defended;
        }

        if (FateTargeting.TryGetFriendlyFateNpc(pinnedId, fateId, out var pinned))
        {
            source = "provisional pinned preparing actor";
            return pinned;
        }

        if (context != null)
        {
            var motivation = ResolveRuntimeFriendlyFateActor(context->MotivationNpc);
            if (motivation != null)
            {
                source = "provisional MotivationNpc";
                return motivation;
            }
        }

        source = "none";
        return null;
    }

    private static IBattleNpc? ResolveRuntimeFriendlyFateActor(uint entityId)
    {
        var obj = FateTargeting.FindByEntityId(entityId);
        return obj is IBattleNpc npc
            && !npc.IsDead
            && npc.MaxHp > 0
            && !FateTargeting.IsAttackableEnemy(npc)
            ? npc
            : null;
    }

    private static Vector3? ResolveReachableProtectedAnchorDestination(Vector3 position)
    {
        var probe = new Vector3(position.X, position.Y + 2f, position.Z);
        var resolved = NavigationGeometry.ProjectReachableGround(
            probe,
            3f,
            position,
            4f,
            3f,
            out var error,
            position.Y,
            3f);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not project protected-objective destination {position} onto the local navmesh layer: {error.Message}");
        return resolved;
    }
}
