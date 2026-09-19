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
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void DriveActiveLeveProfile()
    {
        if (!LeveExecutionProfiles.TryGet(_leveContext.AutomationTarget.LeveId, out var spec))
        {
            FailLeveAutomation($"No execution profile is registered for LeveId={_leveContext.AutomationTarget.LeveId}.");
            return;
        }

        switch (spec.Kind)
        {
            case LeveExecutionKind.BeckonEscort:
                DriveBeckonEscortLeve();
                break;
            case LeveExecutionKind.Rounds:
                DriveLeveRounds(spec);
                break;
            case LeveExecutionKind.DefendCharge:
                DriveDefendChargeLeve(spec);
                break;
            case LeveExecutionKind.TimedCull:
                DriveTimedCullLeve(spec);
                break;
            case LeveExecutionKind.LureAndKill:
                DriveLureAndKillLeve(spec);
                break;
            case LeveExecutionKind.Necrologos:
                DriveNecrologosLeve(spec);
                break;
            default:
                DriveStandardLeveCombat(spec);
                break;
        }
    }
    private unsafe void DriveTimedCullLeve(LeveExecutionSpec spec)
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        if (TryGetTimedCullMinimumState(out var minimumSatisfied, out var snapshot))
        {
            if (!string.Equals(snapshot, _leveContext.TimedCullTodoSnapshot, StringComparison.Ordinal))
            {
                _leveContext.TimedCullTodoSnapshot = snapshot;
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Timed-cull objective state for LeveId={_leveContext.AutomationTarget.LeveId}: {snapshot}.");
            }

            if (minimumSatisfied)
            {
                ClearLeveCombatTarget("timed-cull minimum objectives satisfied");
                if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
                {
                    _automationRun.ReplaceNavigation();
                    ClearLeveNavigationResult();
                }

                _leveContext.AutomationStatus = "Minimum timed-cull objectives satisfied; waiting for the leve timer to expire.";
                if (!_leveContext.TimedCullWaiting)
                {
                    _leveContext.TimedCullWaiting = true;
                    Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Minimum timed-cull objectives are satisfied for LeveId={_leveContext.AutomationTarget.LeveId}; stopping proactive kills and waiting for the timer.");
                }
                return;
            }

            if (_leveContext.TimedCullWaiting)
            {
                _leveContext.TimedCullWaiting = false;
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Timed-cull objective requirements changed for LeveId={_leveContext.AutomationTarget.LeveId}; resuming proactive kills.");
            }
        }
        else if (_leveContext.TimedCullWaiting)
        {
            _leveContext.AutomationStatus = "Timed-cull minimum was satisfied; waiting for objective state to refresh.";
            return;
        }

        DriveStandardLeveCombat(spec);
    }
    private unsafe bool TryGetTimedCullMinimumState(out bool minimumSatisfied, out string snapshot)
    {
        minimumSatisfied = false;
        snapshot = string.Empty;
        var leveDirector = GetActiveLeveDirector();
        if (leveDirector == null)
            return false;

        var objectiveCount = 0;
        var completeCount = 0;
        var states = new List<string>();
        for (var i = 0; i < leveDirector->DirectorTodos.Count; i++)
        {
            ref var todo = ref leveDirector->DirectorTodos[i];
            if (!todo.Enabled)
                continue;

            states.Add($"{todo.Type} current={todo.CurrentCount} needed={todo.NeededCount} complete={todo.Complete}");
            if (todo.Type is not TodoType.FractionBar and not TodoType.Fraction and not TodoType.LargeGrayFraction)
                continue;
            if (todo.NeededCount <= 0)
                continue;

            objectiveCount++;
            var complete = todo.Complete || todo.CurrentCount >= todo.NeededCount;
            if (complete)
                completeCount++;
        }

        minimumSatisfied = objectiveCount > 0 && completeCount == objectiveCount;
        snapshot = objectiveCount > 0
            ? $"minimum={completeCount}/{objectiveCount}; {string.Join(", ", states)}"
            : $"minimum=unknown; {string.Join(", ", states)}";
        return true;
    }
    private unsafe void DriveStandardLeveCombat(LeveExecutionSpec spec)
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        if (TryDriveLeveCombatTarget(GetBestLeveEnemy(spec.PriorityTargetName), "Fighting"))
            return;

        if (_leveContext.CombatTargetId != 0)
        {
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            _leveContext.CombatTargetId = 0;
            _leveContext.NoObjectiveSince = DateTime.Now;
        }
        if (_leveContext.NoObjectiveSince == DateTime.MinValue)
            _leveContext.NoObjectiveSince = DateTime.Now;
        if ((DateTime.Now - _leveContext.NoObjectiveSince).TotalMilliseconds < LeveObjectiveSettleMs)
        {
            _leveContext.AutomationStatus = "No live objective enemy; allowing the next wave to appear.";
            return;
        }
        if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
            return;
        if (DateTime.Now < _leveContext.NextMarkerSweepAt)
            return;

        var markers = GetExactLeveMarkers(_leveContext.AutomationTarget).ToArray();
        if (markers.Length == 0)
        {
            _leveContext.AutomationStatus = "No live objective enemy or exact-name marker; waiting for the leve to update.";
            return;
        }

        Vector3? nextMarker = null;
        var nextMarkerDistance = float.MaxValue;
        foreach (var marker in markers)
        {
            if (_leveContext.VisitedMarkers.Any(visited => NavigationGeometry.HorizontalDistanceSquared(visited, marker) < LeveVisitedMarkerDistance * LeveVisitedMarkerDistance))
                continue;
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, marker);
            if (distance >= nextMarkerDistance)
                continue;
            nextMarker = marker;
            nextMarkerDistance = distance;
        }

        if (nextMarker is not Vector3 destination)
        {
            _leveContext.VisitedMarkers.Clear();
            _leveContext.NextMarkerSweepAt = DateTime.Now.AddMilliseconds(LeveMarkerSweepPauseMs);
            _leveContext.AutomationStatus = "All currently exposed markers were checked; stale markers remain, so starting another sweep after a short pause.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] All {markers.Length} exact-name markers have been visited for LeveId={_leveContext.AutomationTarget.LeveId}; stale markers will not block the next sweep.");
            return;
        }

        if (Player.Object != null
            && (Vector3.Distance(Player.Object.Position, destination) <= LeveMarkerArrivalDistance
                || NavigationGeometry.HorizontalDistanceSquared(Player.Object.Position, destination) <= LeveMarkerHorizontalArrivalDistance * LeveMarkerHorizontalArrivalDistance))
        {
            MarkLeveMarkerVisited(destination);
            _leveContext.NoObjectiveSince = DateTime.Now;
            return;
        }

        _leveContext.FieldNavigationDestination = destination;
        _leveContext.AutomationStatus = $"No live objective enemy here; checking marker ({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1}).";
        if (!StartSimpleMove(destination, false, HandleLeveMarkerNavigationResult, NavigationPurpose.LeveMapFlagTravel, NavigationRestartPolicy.WalkAfterUnstuck))
            _leveContext.FieldNavigationDestination = null;
    }
    private unsafe void DriveLeveRounds(LeveExecutionSpec spec)
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        if (TryDriveLeveCombatTarget(GetBestLeveEnemy(), "Clearing ambush"))
            return;

        ClearLeveCombatTarget("rounds area clear");
        if (FindNearestNamedObject(spec.ObjectiveObjectName, true) is { } destination)
        {
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, destination.Position);
            _leveContext.AutomationStatus = $"Moving to {spec.ObjectiveObjectName} at {distance:F1}y to spring the next ambush.";
            if (distance > LeveRoundsDestinationDistance)
                EnsureLeveProfileNavigation(destination.Position, NavigationPurpose.LeveMapFlagTravel, "rounds destination");
            else
            {
                _automationRun.ReplaceNavigation();
                ClearLeveNavigationResult();
                _leveContext.NoObjectiveSince = DateTime.Now;
            }
            return;
        }

        if (!TryDriveLeveMarkerFallback("No Destination object is loaded; checking the live leve markers."))
            _leveContext.AutomationStatus = "No active ambush or Destination object is loaded; waiting for the next rounds stage.";
    }
    private unsafe void DriveNecrologosLeve(LeveExecutionSpec spec)
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        if (TryDriveLeveCombatTarget(GetBestLeveEnemy(), "Clearing Necrologos wave"))
            return;

        ClearLeveCombatTarget("Necrologos wave clear");
        if (_leveContext.LastParchmentReadAt != DateTime.MinValue
            && (DateTime.Now - _leveContext.LastParchmentReadAt).TotalMilliseconds < LeveParchmentWaveGraceMs)
        {
            _leveContext.AutomationStatus = "Parchment read; holding position while the next Necrologos wave appears.";
            return;
        }

        if (FindNearestNamedObject(spec.ObjectiveObjectName, true) is { } parchment)
        {
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, parchment.Position);
            if (distance > LeveObjectiveObjectInteractDistance)
            {
                _leveContext.AutomationStatus = $"Moving to {spec.ObjectiveObjectName} at {distance:F1}y.";
                EnsureLeveProfileNavigation(parchment.Position, NavigationPurpose.LeveMapFlagTravel, "Necrologos parchment");
                return;
            }

            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            if (Svc.Condition[ConditionFlag.Mounted])
            {
                EnqueueDismount();
                return;
            }

            if (EzThrottler.Throttle("ZBR_LeveParchmentRead", 800))
            {
                TargetSystem.Instance()->Target = (GameObject*)parchment.Address;
                TargetSystem.Instance()->InteractWithObject((GameObject*)parchment.Address, false);
                _leveContext.LastParchmentReadAt = DateTime.Now;
                _leveContext.AutomationStatus = $"Read {parchment.Name.TextValue}; waiting for the summoned wave.";
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Read '{parchment.Name.TextValue}' for Necrologos LeveId={_leveContext.AutomationTarget.LeveId}; waiting for the next wave.");
            }
            return;
        }

        if (!TryDriveLeveMarkerFallback("No Parchment or active wave is loaded; checking the live leve markers."))
            _leveContext.AutomationStatus = "No Parchment or active Necrologos enemy is loaded; waiting for the objective state to update.";
    }
    private unsafe void DriveLureAndKillLeve(LeveExecutionSpec spec)
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        var emergedTarget = GetNearestHostileEnemyByName(spec.EmergedTargetName);
        if (emergedTarget != null)
        {
            ResetLeveLureInteractionSettle();
            TryDriveLeveCombatTarget(emergedTarget, "Slaying emerged target");
            return;
        }

        var haveItem = GetLeveKeyItemCount(spec.EventItemId) > 0;
        if (haveItem)
        {
            var spawnedObjective = GetBestLeveEnemy(excludeName: spec.ItemSourceName);
            if (spawnedObjective != null)
            {
                ResetLeveLureInteractionSettle();
                TryDriveLeveCombatTarget(spawnedObjective, "Clearing spawned lure enemy");
                return;
            }
        }

        if (haveItem && FindNearestNamedObject(spec.PrimeTargetName, true) is { } prime)
        {
            ClearLeveCombatTarget("using lure item");
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, prime.Position);
            TargetSystem.Instance()->Target = (GameObject*)prime.Address;
            if (distance > LeveObjectiveObjectInteractDistance)
            {
                ResetLeveLureInteractionSettle();
                _leveContext.AutomationStatus = $"Carrying lure item {spec.EventItemId}; moving to {prime.Name.TextValue} at {distance:F1}y.";
                EnsureLeveProfileNavigation(prime.Position, NavigationPurpose.LeveMapFlagTravel, "lure prime location");
                return;
            }

            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            if (_leveContext.LurePrimeObjectId != prime.GameObjectId)
            {
                _leveContext.LurePrimeObjectId = prime.GameObjectId;
                _leveContext.LurePrimeInRangeAt = DateTime.Now;
            }
            if (_leveContext.LurePrimeInRangeAt == DateTime.MinValue)
                _leveContext.LurePrimeInRangeAt = DateTime.Now;
            if ((DateTime.Now - _leveContext.LurePrimeInRangeAt).TotalMilliseconds < LeveEventItemStationarySettleMs)
            {
                _leveContext.AutomationStatus = $"In range of {prime.Name.TextValue}; holding still before using lure item {spec.EventItemId}.";
                return;
            }
            if (_leveContext.LastEventItemUseAt != DateTime.MinValue
                && (DateTime.Now - _leveContext.LastEventItemUseAt).TotalMilliseconds < LeveEventItemUseCooldownMs)
            {
                _leveContext.AutomationStatus = $"Used lure item on {prime.Name.TextValue}; waiting for the target to emerge.";
                return;
            }

            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return;
            if (actionManager->GetActionStatus(ActionType.EventItem, spec.EventItemId, prime.GameObjectId) != 0)
            {
                _leveContext.AutomationStatus = $"Lure item {spec.EventItemId} is not usable on {prime.Name.TextValue} yet; waiting.";
                return;
            }

            if (actionManager->UseAction(ActionType.EventItem, spec.EventItemId, prime.GameObjectId))
            {
                _leveContext.LastEventItemUseAt = DateTime.Now;
                _leveContext.AutomationStatus = $"Used lure item {spec.EventItemId} on {prime.Name.TextValue}; waiting for {spec.EmergedTargetName}.";
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Used lure EventItem={spec.EventItemId} on '{prime.Name.TextValue}' for LeveId={_leveContext.AutomationTarget.LeveId}; waiting for '{spec.EmergedTargetName}'.");
            }
            return;
        }

        ResetLeveLureInteractionSettle();
        if (!haveItem)
        {
            var itemSource = GetNearestHostileEnemyByName(spec.ItemSourceName);
            if (itemSource != null)
            {
                TryDriveLeveCombatTarget(itemSource, "Farming lure item from");
                return;
            }
        }

        if (TryDriveLeveCombatTarget(GetBestLeveEnemy(excludeName: haveItem ? spec.ItemSourceName : null), "Clearing lure objective"))
            return;

        ClearLeveCombatTarget("lure area clear");
        if (!TryDriveLeveMarkerFallback(haveItem
                ? $"Holding lure item {spec.EventItemId}; searching for a prime location."
                : $"No {spec.ItemSourceName} is loaded; checking the live leve markers."))
            _leveContext.AutomationStatus = haveItem
                ? $"Holding lure item {spec.EventItemId}; waiting for a prime location to load."
                : $"Waiting for {spec.ItemSourceName} or another lure objective to load.";
    }
    private void ResetLeveLureInteractionSettle()
    {
        _leveContext.LurePrimeObjectId = 0;
        _leveContext.LurePrimeInRangeAt = DateTime.MinValue;
    }
    private unsafe void DriveDefendChargeLeve(LeveExecutionSpec spec)
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out var work))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared while active.");
            return;
        }

        if (TryBeginLeveReturn(work))
            return;

        var charge = FindNearestNamedObject(spec.ProtectedChargeName, false);
        if (TryDriveLeveCombatTarget(GetBestLeveDefenseThreat(charge), "Defending charge from"))
            return;

        ClearLeveCombatTarget("defense wave clear");
        var holdPosition = charge?.Position ?? spec.ProtectedHoldPosition;
        if (holdPosition is Vector3 position)
        {
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, position);
            _leveContext.AutomationStatus = charge != null
                ? $"Holding on protected charge {charge.Name.TextValue}; next wave pending."
                : "Holding at the authored protection point; next wave pending.";
            if (distance > LeveEnemyApproachDistance)
                EnsureLeveProfileNavigation(position, NavigationPurpose.LeveMapFlagTravel, "protected charge");
            else if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
            {
                _automationRun.ReplaceNavigation();
                ClearLeveNavigationResult();
            }
            return;
        }

        if (!TryDriveLeveMarkerFallback("Protected charge is not loaded; holding near the live leve marker."))
            _leveContext.AutomationStatus = "Protected charge is not loaded; waiting for the defense objective to appear.";
    }
    private unsafe bool TryDriveLeveCombatTarget(IBattleNpc? enemy, string statusVerb)
    {
        if (enemy == null)
            return false;

        _leveContext.NoObjectiveSince = DateTime.MinValue;
        _leveContext.NextMarkerSweepAt = DateTime.MinValue;
        if (_leveContext.CombatTargetId != enemy.GameObjectId)
        {
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            _leveContext.CombatTargetId = enemy.GameObjectId;
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Combat target '{enemy.Name.TextValue}' id={enemy.GameObjectId} baseId={enemy.BaseId} selected for LeveId={_leveContext.AutomationTarget.LeveId} profile={GetLeveExecutionProfileName(_leveContext.AutomationTarget.LeveId)}.");
        }

        TargetSystem.Instance()->Target = (GameObject*)enemy.Address;
        StartLeveRotationSolver();
        var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, enemy.Position);
        _leveContext.AutomationStatus = $"{statusVerb} {enemy.Name.TextValue} at {distance:F1}y.";
        if (distance > LeveEnemyApproachDistance && _restartNavigation == null && !VNavmesh.Path.IsRunning() && !VNavmesh.Nav.PathfindInProgress())
        {
            _leveContext.FieldNavigationDestination = enemy.Position;
            StartSimpleMove(enemy.Position, false, HandleLeveObjectiveNavigationResult, NavigationPurpose.TargetFollow, NavigationRestartPolicy.WalkAfterUnstuck);
        }
        else if (distance <= LeveEnemyApproachDistance && _restartNavigation != null)
        {
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
        }
        return true;
    }
    private void ClearLeveCombatTarget(string reason)
    {
        if (_leveContext.CombatTargetId != 0)
        {
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
            _leveContext.CombatTargetId = 0;
        }
        StopLeveRotationSolver(reason);
    }
    private bool TryDriveLeveMarkerFallback(string status)
    {
        if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
            return true;
        if (DateTime.Now < _leveContext.NextMarkerSweepAt)
            return true;

        var markers = GetExactLeveMarkers(_leveContext.AutomationTarget).ToArray();
        if (markers.Length == 0)
            return false;

        Vector3? nextMarker = null;
        var nextMarkerDistance = float.MaxValue;
        foreach (var marker in markers)
        {
            if (_leveContext.VisitedMarkers.Any(visited => NavigationGeometry.HorizontalDistanceSquared(visited, marker) < LeveVisitedMarkerDistance * LeveVisitedMarkerDistance))
                continue;
            var distance = Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, marker);
            if (distance >= nextMarkerDistance)
                continue;
            nextMarker = marker;
            nextMarkerDistance = distance;
        }

        if (nextMarker is not Vector3 destination)
        {
            _leveContext.VisitedMarkers.Clear();
            _leveContext.NextMarkerSweepAt = DateTime.Now.AddMilliseconds(LeveMarkerSweepPauseMs);
            _leveContext.AutomationStatus = status;
            return true;
        }

        if (Player.Object != null
            && (Vector3.Distance(Player.Object.Position, destination) <= LeveMarkerArrivalDistance
                || NavigationGeometry.HorizontalDistanceSquared(Player.Object.Position, destination) <= LeveMarkerHorizontalArrivalDistance * LeveMarkerHorizontalArrivalDistance))
        {
            MarkLeveMarkerVisited(destination);
            return true;
        }

        _leveContext.FieldNavigationDestination = destination;
        _leveContext.AutomationStatus = status;
        if (!StartSimpleMove(destination, false, HandleLeveMarkerNavigationResult, NavigationPurpose.LeveMapFlagTravel, NavigationRestartPolicy.WalkAfterUnstuck))
            _leveContext.FieldNavigationDestination = null;
        return true;
    }
    private static string GetLeveExecutionProfileName(uint leveId)
        => LeveExecutionProfiles.GetName(leveId);
}
