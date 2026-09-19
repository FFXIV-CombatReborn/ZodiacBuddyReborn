using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using System;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe void DriveDefendFateCombat(IFate fate)
    {
        var defendThreat = FateTargeting.GetBestDefendObjectiveThreat(
            fate.FateId,
            _fateContext.CombatTargetId,
            _fateContext.DefendAnchorId,
            FateDefendObjectiveSwitchHpMargin);
        if (defendThreat.Target != null && defendThreat.ProtectedObject != null)
        {
            if (_fateCombatTargets.ThreatClearActive)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Defend objective pressure preempted player/chocobo threat-clear for FateId={fate.FateId}; protected objective threats have priority.");
                _fateCombatTargets.Reset();
            }

            var protectedObject = defendThreat.ProtectedObject;
            if (_fateContext.DefendAnchorId != protectedObject.GameObjectId)
            {
                _fateContext.DefendAnchorId = protectedObject.GameObjectId;
                var hp = protectedObject is IBattleNpc battleNpc ? $" hp={battleNpc.CurrentHp}/{battleNpc.MaxHp}" : string.Empty;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Defend priority objective '{protectedObject.Name.TextValue}' kind={protectedObject.ObjectKind} id={protectedObject.GameObjectId}{hp} selected with {defendThreat.ProtectedAttackerCount} attacker(s) for FateId={fate.FateId}.");
            }

            if (_fateContext.CombatTargetId != defendThreat.Target.GameObjectId)
            {
                var protectedHp = protectedObject is IBattleNpc battleNpc
                    ? $" hp={battleNpc.CurrentHp}/{battleNpc.MaxHp}"
                    : string.Empty;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Defend objective threat '{defendThreat.Target.Name.TextValue}' id={defendThreat.Target.GameObjectId} hp={defendThreat.Target.CurrentHp}/{defendThreat.Target.MaxHp} targeting '{protectedObject.Name.TextValue}' id={protectedObject.GameObjectId}{protectedHp}; objectiveAttackers={defendThreat.ProtectedAttackerCount}, totalObjectiveThreats={defendThreat.TotalObjectiveThreatCount}.");
            }

            _fateContext.IncidentalAggroTargetId = 0;
            DriveFateEnemy(fate, defendThreat.Target);
            return;
        }

        if (TryDriveFateThreatClear(fate))
            return;

        var target = FateTargeting.GetBestCombatTarget(fate.FateId, _fateContext.CombatTargetId);
        if (target != null)
        {
            _fateContext.IncidentalAggroTargetId = 0;
            if (_fateContext.CombatTargetId != target.GameObjectId)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Defend fallback target '{target.Name.TextValue}' id={target.GameObjectId} hp={target.CurrentHp}/{target.MaxHp} selected because no protected objective is currently under attack.");
            DriveFateEnemy(fate, target);
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            var incidentalTarget = FateTargeting.GetBestIncidentalAggroTarget(
                fate.FateId,
                _fateContext.IncidentalAggroTargetId,
                Player.Object!.Position,
                FateDefendIncidentalAggroLeashDistance);
            if (incidentalTarget != null)
            {
                if (_fateContext.IncidentalAggroTargetId != incidentalTarget.GameObjectId)
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Defend incidental aggro target '{incidentalTarget.Name.TextValue}' id={incidentalTarget.GameObjectId} fateId={FateTargeting.GetFateId(incidentalTarget)} acquired after objective threats cleared.");
                _fateContext.IncidentalAggroTargetId = incidentalTarget.GameObjectId;
                DriveFateEnemy(fate, incidentalTarget, FateNavigationIntent.IncidentalAggro, "defend incidental attacker");
                return;
            }
        }

        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        _fateCombatEngagement.Reset();
        ReleaseFateTacticalMovement();
        if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
            RequestFateRotationSolverStop("Defend FATE has no committed combat target");

        var anchor = ResolveFateDefendAnchor(fate.FateId, _fateContext.DefendAnchorId, out var source);
        if (anchor == null)
        {
            DriveDefendWithoutResolvedAnchor(fate);
            return;
        }

        if (_fateContext.DefendAnchorId != anchor.GameObjectId)
        {
            _fateContext.DefendAnchorId = anchor.GameObjectId;
            var hp = anchor is IBattleNpc battleNpc ? $" hp={battleNpc.CurrentHp}/{battleNpc.MaxHp}" : string.Empty;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Defend idle anchor resolved from {source}: '{anchor.Name.TextValue}' kind={anchor.ObjectKind} id={anchor.GameObjectId}{hp} for FateId={fate.FateId}.");
        }

        var player = Player.Object!;
        var anchorDistanceSquared = NavigationGeometry.HorizontalDistanceSquared(player.Position, anchor.Position);
        var returning = _fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.DefendAnchor;
        var returnThreshold = returning ? FateDefendReturnStopDistance : FateDefendReturnStartDistance;
        if (anchorDistanceSquared > returnThreshold * returnThreshold)
        {
            var destination = ResolveReachableProtectedAnchorDestination(anchor.Position) ?? anchor.Position;
            EnsureFateNavigation(destination, false, $"defend anchor {fate.FateId}", FateNavigationIntent.DefendAnchor);
            var distance = MathF.Sqrt(anchorDistanceSquared);
            _fateContext.AutomationStatus = $"Repositioning toward defended objective {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)} ({distance:F1}y).";
            return;
        }

        StopFateOwnedNavigation();
        _fateContext.AutomationStatus = $"Holding the defend area around {anchor.Name.TextValue} in {FateMetadata.GetName(fate.FateId)}.";
    }

    private void DriveDefendWithoutResolvedAnchor(IFate fate)
    {
        var centerDestination = ResolveReachableFateDestination(fate.Position, Math.Min(12f, Math.Max(6f, fate.Radius * 0.25f)), fate.Position.Y) ?? fate.Position;
        var centerDistanceSquared = NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, centerDestination);
        var returning = _fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.DefendAnchor;
        var returnThreshold = returning ? FateDefendReturnStopDistance : FateDefendReturnStartDistance;
        if (centerDistanceSquared > returnThreshold * returnThreshold)
        {
            EnsureFateNavigation(centerDestination, false, $"Defend FATE center {fate.FateId}", FateNavigationIntent.DefendAnchor);
            _fateContext.AutomationStatus = $"Defend FATE {FateMetadata.GetName(fate.FateId)} has no resolved protected objective; repositioning toward its center while waiting to reacquire one.";
            return;
        }

        StopFateOwnedNavigation();
        _fateContext.AutomationStatus = $"Defend FATE {FateMetadata.GetName(fate.FateId)} has no resolved protected objective; holding the combat area while waiting for objective pressure.";
    }

    private void PinFateDefendAnchor(ushort fateId, IGameObject npc)
    {
        if (!IsUsableDefendAnchor(npc, fateId, false))
            return;

        if (_fateContext.DefendAnchorId == npc.GameObjectId)
            return;

        _fateContext.DefendAnchorId = npc.GameObjectId;
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Pinned preparing Defend starter/protected objective '{npc.Name.TextValue}' kind={npc.ObjectKind} id={npc.GameObjectId} for FateId={fateId}.");
    }

    private unsafe IGameObject? ResolveFateDefendAnchor(ushort fateId, ulong pinnedId, out string source)
    {
        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        if (context != null)
        {
            var objective = ResolveRuntimeDefendAnchor(context->ObjectiveNpc, fateId);
            if (objective != null)
            {
                source = "ObjectiveNpc";
                return objective;
            }
        }

        if (pinnedId != 0
            && FateTargeting.TryGetObject(pinnedId, out var pinned)
            && IsUsableDefendAnchor(pinned, fateId, true))
        {
            source = "sticky protected objective";
            return pinned;
        }

        var targeted = FateTargeting.GetMostTargetedFateObject(fateId);
        if (targeted != null)
        {
            source = "same-FATE objective currently targeted by hostile FATE actors";
            return targeted;
        }

        if (context != null)
        {
            var motivation = ResolveRuntimeDefendAnchor(context->MotivationNpc, fateId);
            if (motivation != null)
            {
                source = "provisional MotivationNpc";
                return motivation;
            }
        }

        source = "none";
        return null;
    }

    private static IGameObject? ResolveRuntimeDefendAnchor(uint entityId, ushort fateId)
    {
        var obj = FateTargeting.FindByEntityId(entityId);
        return obj != null && IsUsableDefendAnchor(obj, fateId, false) ? obj : null;
    }

    private static bool IsUsableDefendAnchor(IGameObject obj, ushort fateId, bool requireFateTag)
    {
        if (obj.Address == nint.Zero)
            return false;
        if (requireFateTag && FateTargeting.GetFateId(obj) != fateId)
            return false;
        if (obj is IBattleNpc battleNpc)
            return !battleNpc.IsDead && battleNpc.CurrentHp != 0 && battleNpc.MaxHp > 0 && !FateTargeting.IsAttackableEnemy(battleNpc);
        return true;
    }
}
