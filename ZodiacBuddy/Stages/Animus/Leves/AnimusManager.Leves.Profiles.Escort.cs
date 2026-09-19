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

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe void DriveBeckonEscortLeve()
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        var escort = GetLeveEscortActor();
        if (escort == null)
        {
            StopLeveRotationSolver("waiting for the escort objective actor");
            _leveContext.AutomationStatus = "Waiting for the 71244-tagged friendly escort actor to load.";
            return;
        }

        if (_leveContext.EscortActorId != escort.GameObjectId)
        {
            _leveContext.EscortActorId = escort.GameObjectId;
            ResetLeveEscortMotionTracking();
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Escort acquired: '{escort.Name.TextValue}' ({escort.GameObjectId}).");
        }

        UpdateLeveEscortMotion(escort);

        var threat = GetBestLeveEscortThreat(escort);
        if (threat != null)
        {
            if (_leveContext.CombatTargetId != threat.GameObjectId)
            {
                _automationRun.ReplaceNavigation();
                ClearLeveNavigationResult();
                _leveContext.EscortLeadNavigationActive = false;
                _leveContext.CombatTargetId = threat.GameObjectId;
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Escort threat selected: '{threat.Name.TextValue}' ({threat.GameObjectId}).");
            }

            TargetSystem.Instance()->Target = (GameObject*)threat.Address;
            StartLeveRotationSolver();
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, threat.Position);
            _leveContext.AutomationStatus = $"Protecting {escort.Name.TextValue} from {threat.Name.TextValue} at {distance:F1}y.";
            if (distance > LeveEnemyApproachDistance && _restartNavigation == null && !VNavmesh.Path.IsRunning() && !VNavmesh.Nav.PathfindInProgress())
            {
                _leveContext.FieldNavigationDestination = threat.Position;
                StartSimpleMove(threat.Position, false, HandleLeveObjectiveNavigationResult, NavigationPurpose.TargetFollow, NavigationRestartPolicy.WalkAfterUnstuck);
            }
            return;
        }

        if (_leveContext.CombatTargetId != 0)
        {
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            _leveContext.EscortLeadNavigationActive = false;
            _leveContext.CombatTargetId = 0;
        }
        StopLeveRotationSolver("escort area clear");

        var player = Player.Object;
        if (player == null)
            return;

        var distanceToEscort = Vector3.Distance(player.Position, escort.Position);
        if (_leveContext.EscortLeadNavigationActive)
        {
            if (distanceToEscort >= LeveEscortLeadDistance)
            {
                _automationRun.ReplaceNavigation();
                ClearLeveNavigationResult();
                _leveContext.EscortLeadNavigationActive = false;
                _leveContext.AutomationStatus = $"Reached {distanceToEscort:F1}y lead over {escort.Name.TextValue}; stopping to beckon.";
            }
            return;
        }

        if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
            return;

        if (distanceToEscort > LeveEscortBeckonDistance)
        {
            _leveContext.AutomationStatus = $"Escort is {distanceToEscort:F1}y away; moving back into beckon range.";
            EnsureLeveFieldNavigation(escort.Position, NavigationPurpose.TargetFollow, "escort catch-up");
            return;
        }

        var goal = ResolveLeveEscortGoal(escort);
        if (goal is not Vector3 destination)
        {
            _leveContext.AutomationStatus = $"Near {escort.Name.TextValue}; waiting for the escort destination marker.";
            TryBeckonLeveEscort(escort);
            return;
        }

        var escortToGoal = Vector3.Distance(escort.Position, destination);
        var playerToGoal = Vector3.Distance(player.Position, destination);
        var escortNearFinish = escortToGoal <= LeveEscortFinishApproachDistance
            || playerToGoal <= LeveMarkerArrivalDistance && distanceToEscort <= LeveEscortFinishApproachDistance;
        if (escortNearFinish)
        {
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            _leveContext.EscortLeadNavigationActive = false;
            if (_leveContext.EscortFinishReachedAt == DateTime.MinValue)
                _leveContext.EscortFinishReachedAt = DateTime.Now;

            var finishSettleMs = (DateTime.Now - _leveContext.EscortFinishReachedAt).TotalMilliseconds;
            if (finishSettleMs < LeveEscortFinishSettleMs)
            {
                _leveContext.AutomationStatus = $"{escort.Name.TextValue} is at the destination; waiting for the game to register completion.";
                return;
            }

            _leveContext.AutomationStatus = $"Escort finish has not registered after {LeveEscortFinishSettleMs / 1000}s; trying one recovery beckon.";
            if (TryBeckonLeveEscort(escort))
                _leveContext.EscortFinishReachedAt = DateTime.Now;
            return;
        }

        _leveContext.EscortFinishReachedAt = DateTime.MinValue;
        if (playerToGoal <= LeveMarkerArrivalDistance)
        {
            _leveContext.AutomationStatus = $"At the escort finish; beckoning {escort.Name.TextValue} through the destination.";
            TryBeckonLeveEscort(escort);
            return;
        }

        if (IsLeveEscortMoving(escort))
        {
            _leveContext.AutomationStatus = $"Waiting for {escort.Name.TextValue} to finish moving before the next escort leg.";
            return;
        }

        if (distanceToEscort >= LeveEscortLeadDistance)
        {
            _leveContext.AutomationStatus = $"Holding about {distanceToEscort:F1}y ahead of {escort.Name.TextValue}; beckoning it forward.";
            TryBeckonLeveEscort(escort);
            return;
        }

        if (_leveContext.LastBeckonAt != DateTime.MinValue && distanceToEscort > LeveEscortCatchupDistance)
        {
            _leveContext.AutomationStatus = $"{escort.Name.TextValue} stopped {distanceToEscort:F1}y away; beckoning it closer before continuing.";
            TryBeckonLeveEscort(escort);
            return;
        }

        _leveContext.FieldNavigationDestination = destination;
        _leveContext.EscortLeadNavigationActive = true;
        _leveContext.AutomationStatus = $"Following a full ground path toward the escort destination until about {LeveEscortLeadDistance:F0}y ahead of {escort.Name.TextValue}.";
        if (!StartSimpleMove(destination, false, HandleLeveEscortLeadNavigationResult, NavigationPurpose.LeveMapFlagTravel, NavigationRestartPolicy.WalkAfterUnstuck))
        {
            _leveContext.EscortLeadNavigationActive = false;
            _leveContext.FieldNavigationDestination = null;
        }
    }
    private void HandleLeveEscortLeadNavigationResult(AnimusNavigationResult result)
    {
        _leveContext.EscortLeadNavigationActive = false;
        ClearLeveNavigationResult();
        if (result == AnimusNavigationResult.Arrived)
        {
            _leveContext.AutomationStatus = "Reached the escort path endpoint; waiting to beckon the escort through the finish.";
            return;
        }
        if (result != AnimusNavigationResult.Cancelled)
            _leveContext.AutomationStatus = $"Escort lead navigation ended with {result}; recalculating from the escort's current position.";
    }
    private unsafe IBattleNpc? GetLeveEscortActor()
    {
        var leveDirector = GetActiveLeveDirector();
        if (leveDirector == null)
            return null;

        var player = Player.Object;
        IBattleNpc? fallback = null;
        var fallbackDistance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc
                || npc.IsDead
                || npc.CurrentHp == 0
                || !npc.IsTargetable
                || FateTargeting.GetNameplateIconId(npc) != LeveEnemyNameplateIconId
                || FateTargeting.IsHostileEnemy(npc)
                || !BelongsToLeveDirector(npc, leveDirector))
                continue;

            if (_leveContext.AutomationTarget.LeveId == BeckonEscortLeveTestId && npc.BaseId == BeckonEscortLeveNpcBaseId)
                return npc;

            var distance = player == null ? 0f : Vector3.Distance(player.Position, npc.Position);
            if (distance >= fallbackDistance)
                continue;
            fallback = npc;
            fallbackDistance = distance;
        }
        return fallback;
    }
    private static IBattleNpc? GetBestLeveEscortThreat(IBattleNpc escort)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        var playerId = player.GameObjectId;
        var escortId = escort.GameObjectId;
        var leashSquared = LeveEscortThreatLeashDistance * LeveEscortThreatLeashDistance;
        IBattleNpc? best = null;
        var bestPriority = int.MaxValue;
        var bestDistance = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || !FateTargeting.IsAttackableEnemy(npc))
                continue;

            var targetsEscort = npc.TargetObjectId == escortId;
            var targetsPlayer = npc.TargetObjectId == playerId;
            if (!targetsEscort && !targetsPlayer)
                continue;

            var escortDistance = Vector3.DistanceSquared(escort.Position, npc.Position);
            var playerDistance = Vector3.DistanceSquared(player.Position, npc.Position);
            if (escortDistance > leashSquared && playerDistance > leashSquared)
                continue;

            var priority = targetsEscort ? 0 : 1;
            var distance = Math.Min(escortDistance, playerDistance);
            if (priority > bestPriority || priority == bestPriority && distance >= bestDistance)
                continue;

            best = npc;
            bestPriority = priority;
            bestDistance = distance;
        }

        return best;
    }
    private Vector3? ResolveLeveEscortGoal(IBattleNpc escort)
    {
        var markers = GetExactLeveMarkers(_leveContext.AutomationTarget);
        Vector3? markerGoal = null;
        var markerDistanceFromStart = 0f;
        foreach (var marker in markers)
        {
            var distanceFromStart = _leveContext.StartMarker == Vector3.Zero
                ? Vector3.DistanceSquared(escort.Position, marker)
                : NavigationGeometry.HorizontalDistanceSquared(_leveContext.StartMarker, marker);
            if (distanceFromStart <= 25f * 25f || distanceFromStart <= markerDistanceFromStart)
                continue;
            markerGoal = marker;
            markerDistanceFromStart = distanceFromStart;
        }

        Vector3? resolved = markerGoal;
        if (!resolved.HasValue && _leveContext.AutomationTarget.LeveId == BeckonEscortLeveTestId)
            resolved = BeckonEscortFallbackGoal;
        if (resolved is not Vector3 goal)
            return null;

        if (_leveContext.EscortGoal is not Vector3 previous || NavigationGeometry.HorizontalDistanceSquared(previous, goal) > 4f)
        {
            _leveContext.EscortGoal = goal;
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Escort destination resolved from {(markerGoal.HasValue ? "live marker" : "fallback")}.");
        }
        return _leveContext.EscortGoal;
    }
    private unsafe bool TryBeckonLeveEscort(IBattleNpc escort)
    {
        var player = Player.Object;
        if (player == null || Vector3.Distance(player.Position, escort.Position) > LeveEscortBeckonDistance)
            return false;
        if (IsLeveEscortMoving(escort))
        {
            if (!_leveContext.EscortMovementLogged)
            {
                _leveContext.EscortMovementLogged = true;
                Service.PluginLog.Verbose("[ZodiacBuddy/LEVE] Waiting for escort to stop before /beckon.");
            }
            return false;
        }
        _leveContext.EscortMovementLogged = false;
        if (_leveContext.LastBeckonAt != DateTime.MinValue && (DateTime.Now - _leveContext.LastBeckonAt).TotalMilliseconds < LeveEscortBeckonCooldownMs)
            return false;

        TargetSystem.Instance()->Target = (GameObject*)escort.Address;
        Chat.ExecuteCommand("/beckon");
        _leveContext.LastBeckonAt = DateTime.Now;
        Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] /beckon issued to '{escort.Name.TextValue}' from {Vector3.Distance(player.Position, escort.Position):F1}y.");
        return true;
    }
    private void ResetLeveEscortMotionTracking()
    {
        _leveContext.EscortMotionActorId = 0;
        _leveContext.EscortMotionPosition = Vector3.Zero;
        _leveContext.EscortMotionSampleAt = DateTime.MinValue;
        _leveContext.EscortLastMovedAt = DateTime.MinValue;
        _leveContext.EscortMovementLogged = false;
    }
    private void UpdateLeveEscortMotion(IBattleNpc escort)
    {
        var now = DateTime.Now;
        if (_leveContext.EscortMotionActorId != escort.GameObjectId || _leveContext.EscortMotionSampleAt == DateTime.MinValue)
        {
            _leveContext.EscortMotionActorId = escort.GameObjectId;
            _leveContext.EscortMotionPosition = escort.Position;
            _leveContext.EscortMotionSampleAt = now;
            _leveContext.EscortLastMovedAt = now;
            return;
        }

        if ((now - _leveContext.EscortMotionSampleAt).TotalMilliseconds < LeveEscortMovementSampleMs)
            return;

        var moved = NavigationGeometry.HorizontalDistanceSquared(_leveContext.EscortMotionPosition, escort.Position) >= LeveEscortMovementThreshold * LeveEscortMovementThreshold;
        _leveContext.EscortMotionPosition = escort.Position;
        _leveContext.EscortMotionSampleAt = now;
        if (moved)
            _leveContext.EscortLastMovedAt = now;
    }
    private bool IsLeveEscortMoving(IBattleNpc escort)
    {
        UpdateLeveEscortMotion(escort);
        return _leveContext.EscortLastMovedAt == DateTime.MinValue
            || (DateTime.Now - _leveContext.EscortLastMovedAt).TotalMilliseconds < LeveEscortStationarySettleMs;
    }
    private void HandleLeveObjectiveNavigationResult(AnimusNavigationResult result)
    {
        ClearLeveNavigationResult();
        if (result is not (AnimusNavigationResult.Arrived or AnimusNavigationResult.Cancelled))
            _leveContext.AutomationStatus = $"Objective-target navigation ended with {result}; rescanning.";
    }
    private void HandleLeveMarkerNavigationResult(AnimusNavigationResult result)
    {
        var destination = _leveContext.FieldNavigationDestination;
        ClearLeveNavigationResult();
        if (result == AnimusNavigationResult.Arrived && destination is Vector3 marker)
        {
            MarkLeveMarkerVisited(marker);
            _leveContext.NoObjectiveSince = DateTime.Now;
            return;
        }
        if (result != AnimusNavigationResult.Cancelled)
        {
            _leveContext.NextMarkerSweepAt = DateTime.Now.AddMilliseconds(LeveMarkerRetryPauseMs);
            _leveContext.AutomationStatus = $"Marker navigation ended with {result}; rescanning after a short pause.";
        }
    }
}
