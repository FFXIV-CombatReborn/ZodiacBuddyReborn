using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXVec3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ZodiacBuddy.Stages.Animus.Data;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void MonitorUnstuck(IFramework _)
    {
        if (Player.Object == null) return;
        switch (_unstuckPhase)
        {
            case UnstuckPhase.Idle:
                return;

            case UnstuckPhase.AwaitingPathStart:
                if (!VNavmesh.Nav.PathfindInProgress()    
                    && VNavmesh.Path.IsRunning()           
                    && VNavmesh.Path.NumWaypoints() > 0)  
                {
                    _armPos = Player.Object.Position;
                    LogInitialEnemyMapFlagPathStart(_restartNavigation, VNavmesh.Path.NumWaypoints());
                    _unstuckPhase = UnstuckPhase.AwaitingFirstMovement;
                }
                return;

            case UnstuckPhase.AwaitingFirstMovement:
                if (Vector3.Distance(_armPos, Player.Object.Position) >= MinMovementDistance)
                {
                    _lastPosition = Player.Object.Position;
                    _lastMovement = DateTime.Now; 
                    _unstuckPhase = UnstuckPhase.Active;
                }
                return;

            case UnstuckPhase.Active:
                break; 
        }
        if (!IsPathing || _advancedUnstuck.IsRunning) return;

        var now = DateTime.Now;
        var currentPos = Player.Object.Position;
        if (TryHandleOuterLaNosceaMineBoundaryTransition(currentPos))
            return;

        if (Vector3.Distance(_lastPosition, currentPos) >= MinMovementDistance)
        {
            _lastPosition = currentPos;
            _lastMovement = now;
        }
        else if ((now - _lastMovement).TotalSeconds > NavResetThresholdSeconds)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] AdvancedUnstuck: stuck detected. Moved {Vector3.Distance(_lastPosition, currentPos)} yalms in {(now - _lastMovement).TotalSeconds:F1} seconds.");
            BeginUnstuckRecovery();
            _lastMovement = now;
        }
    }
    private void BeginUnstuckRecovery()
    {
        var descriptor = _restartNavigation;
        if (descriptor == null || !_automationRun.IsActive(descriptor.RunId))
            return;

        if (descriptor.UnstuckAttempts >= MaxUnstuckAttempts)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/NAV] Unstuck recovery exhausted for {descriptor.Purpose} after {descriptor.UnstuckAttempts} attempts.");
            _automationRun.ReplaceNavigation();
            descriptor.Completed(AnimusNavigationResult.StoppedBeforeArrival);
            if (_automationRun.IsActive(descriptor.RunId))
                CancelActiveRun();
            return;
        }

        var recovery = descriptor.WithUnstuckAttempt();
        _restartNavigation = recovery;
        Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] Starting unstuck recovery {recovery.UnstuckAttempts}/{MaxUnstuckAttempts} for {recovery.Purpose}.");
        _advancedUnstuck.Start();
    }
    private bool StartSimpleMove(Vector3 destination, bool fly, Action<AnimusNavigationResult> completed, NavigationPurpose purpose, NavigationRestartPolicy restartPolicy, Func<Vector3?>? destinationResolver = null, Action<NavigationRestartDescriptor, AnimusNavigationResult>? terminalResultHandler = null, int unstuckAttempts = 0, float arrivalDistance = 3f)
    {
        var descriptor = new NavigationRestartDescriptor(_automationRun.ActiveRunId, destination, destinationResolver, fly, completed, purpose, restartPolicy, terminalResultHandler, unstuckAttempts, false, arrivalDistance);
        _restartNavigation = descriptor;
        var activeEnemyTarget = _activeEnemyTarget;
        var accepted = _automationRun.TryStartNavigation(destination, fly, () => _advancedUnstuck.IsRunning, result => CompleteNavigationResult(descriptor, result), arrivalDistance);
        LogInitialEnemyMapFlagNavigation(descriptor, activeEnemyTarget, accepted);
        if (!accepted)
        {
            if (ReferenceEquals(_restartNavigation, descriptor))
                _restartNavigation = null;
            return false;
        }
        if (_automationRun.State == AnimusAutomationRunState.Arrived)
            return true;

        _unstuckPhase = UnstuckPhase.AwaitingPathStart;
        StartUnstuckMonitoring();
        return true;
    }
    private bool StartGroundGuidedFlight(Vector3 destination, Action<AnimusNavigationResult> completed, NavigationPurpose purpose, NavigationRestartPolicy restartPolicy, Func<Vector3?>? destinationResolver = null, Action<NavigationRestartDescriptor, AnimusNavigationResult>? terminalResultHandler = null, int unstuckAttempts = 0)
    {
        if (Player.Object is not { } player)
            return false;

        var pathStart = ResolveGroundGuidedPathStart(player.Position);
        if (pathStart is not Vector3 groundStart)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight projection was unavailable for {destination}; using ground navigation.");
            return StartSimpleMove(destination, false, completed, purpose, restartPolicy, destinationResolver, terminalResultHandler, unstuckAttempts);
        }

        var descriptor = new NavigationRestartDescriptor(_automationRun.ActiveRunId, destination, destinationResolver, true, completed, purpose, restartPolicy, terminalResultHandler, unstuckAttempts, true);
        _restartNavigation = descriptor;
        var activeEnemyTarget = _activeEnemyTarget;
        var accepted = _automationRun.TryStartGroundGuidedFlight(groundStart, destination, GroundGuidedFlightClearance, () => _advancedUnstuck.IsRunning, result => CompleteGroundGuidedNavigationResult(descriptor, result));
        LogInitialEnemyMapFlagNavigation(descriptor, activeEnemyTarget, accepted);
        if (!accepted)
        {
            if (ReferenceEquals(_restartNavigation, descriptor))
                _restartNavigation = null;
            return false;
        }
        if (_automationRun.State == AnimusAutomationRunState.Arrived)
            return true;

        Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight requested from projected ground {groundStart} to {destination} with {GroundGuidedFlightClearance:F1}y clearance.");
        _unstuckPhase = UnstuckPhase.AwaitingPathStart;
        StartUnstuckMonitoring();
        return true;
    }
    private void CompleteGroundGuidedNavigationResult(NavigationRestartDescriptor descriptor, AnimusNavigationResult result)
    {
        if ((result is AnimusNavigationResult.Rejected or AnimusNavigationResult.StartupTimedOut or AnimusNavigationResult.StoppedBeforeArrival)
            && _automationRun.IsActive(descriptor.RunId)
            && ReferenceEquals(_restartNavigation, descriptor))
        {
            var destination = descriptor.DestinationResolver?.Invoke() ?? descriptor.Destination;
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight ended with {result}; using mounted ground navigation.");
            StartSimpleMove(destination, false, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts, descriptor.ArrivalDistance);
            return;
        }

        CompleteNavigationResult(descriptor, result);
    }
    private Vector3? ResolveGroundGuidedPathStart(Vector3 playerPosition)
    {
        var probe = new Vector3(playerPosition.X, playerPosition.Y + 3f, playerPosition.Z);
        var resolved = NavigationGeometry.ProjectReachableGround(
            probe,
            6f,
            playerPosition,
            8f,
            500f,
            out var error);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight could not resolve a reachable ground start: {error.Message}");
        return resolved;
    }
    private void CompleteNavigationResult(NavigationRestartDescriptor descriptor, AnimusNavigationResult result)
    {
        if (descriptor.TerminalResultHandler != null)
        {
            descriptor.TerminalResultHandler(descriptor, result);
            return;
        }

        descriptor.Completed(result);
    }
    private void HandleTravelNavigationResult(AnimusNavigationResult result)
    {
        if (result == AnimusNavigationResult.Arrived)
        {
            _restartNavigation = null;
            CompleteTravelNavigation();
            return;
        }
        if (result == AnimusNavigationResult.Cancelled)
            return;

        Service.PluginLog.Warning($"[ZodiacBuddy/NAV] Navigation ended with {result}.");
        CancelActiveRun();
    }
    private void HandleFollowupNavigationResult(AnimusNavigationResult result)
    {
        if (result == AnimusNavigationResult.Arrived)
        {
            _restartNavigation = null;
            return;
        }
        if (result == AnimusNavigationResult.Cancelled)
            return;

        Service.PluginLog.Warning($"[ZodiacBuddy/NAV] Follow-up navigation ended with {result}.");
        CancelActiveRun();
    }
    private static bool SupportsGroundGuidedFlightRecovery(NavigationPurpose purpose)
        => purpose is NavigationPurpose.InitialEnemyMapFlagTravel
            or NavigationPurpose.EnemySpawnRelocation
            or NavigationPurpose.TargetFollow
            or NavigationPurpose.FateTravel;
    private void RestartOwnedNavigation()
    {
        var descriptor = _restartNavigation;
        if (descriptor == null || !_automationRun.IsActive(descriptor.RunId))
            return;

        _automationRun.ReplaceNavigation();
        if (descriptor.Purpose == NavigationPurpose.TargetFollow
            && descriptor.UnstuckAttempts >= MaxUnstuckAttempts
            && TryStartEnemyTargetFollowFlightRecovery(descriptor))
            return;

        var destination = descriptor.DestinationResolver == null
            ? descriptor.Destination
            : descriptor.DestinationResolver();
        if (destination is not Vector3 point)
        {
            CompleteNavigationResult(descriptor, AnimusNavigationResult.Rejected);
            return;
        }

        if (SupportsGroundGuidedFlightRecovery(descriptor.Purpose) && descriptor.GroundGuided)
        {
            StartSimpleMove(point, false, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts, descriptor.ArrivalDistance);
            return;
        }

        if (SupportsGroundGuidedFlightRecovery(descriptor.Purpose) && descriptor.Fly && descriptor.UnstuckAttempts == 1)
        {
            StartGroundGuidedFlight(point, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts);
            return;
        }

        var fly = descriptor.RestartPolicy == NavigationRestartPolicy.WalkAfterUnstuck
            ? false
            : descriptor.Fly;
        StartSimpleMove(point, fly, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts, descriptor.ArrivalDistance);
    }
    private unsafe void EnqueueDismount()
    {
        if (_advancedUnstuck.IsRunning)
        {
            Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Skipping dismount because AdvancedUnstuck is active.");
            return;
        }
        var am = ActionManager.Instance();
        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0);
        }, "Dismount");
        _automationRun.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Skipping Wait for not in flight because AdvancedUnstuck active.");
                return true;
            }
            return !Svc.Condition[ConditionFlag.InFlight] && CanAct;
        }, 1000, "Wait for not in flight");
        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0);
        }, "Dismount 2");
        _automationRun.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Skipping Wait for dismount because AdvancedUnstuck active.");
                return true;
            }
            return !Svc.Condition[ConditionFlag.Mounted] && CanAct;
        }, 1000, "Wait for dismount");
        _automationRun.Enqueue(() =>
        {
            if (!Svc.Condition[ConditionFlag.Mounted])
                _automationRun.DelayNextImmediate(500);
        });
    }
    private void OnUnstuckCompleteHandler()
    {
        if (!_automationRun.HasActiveRun)
            return;

        Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Unstuck finished, restarting navigation.");
        RestartOwnedNavigation();
    }
    private void StartUnstuckMonitoring()
    {
        if (!_monitoringUnstuck)
        {
            _monitoringUnstuck = true;
            _lastPosition = Player.Object?.Position ?? Vector3.Zero;
            _lastMovement = DateTime.Now;
            Svc.Framework.Update += MonitorUnstuck;
        }
    }
    private void StopUnstuckMonitoring()
    {
        if (_monitoringUnstuck)
        {
            Svc.Framework.Update -= MonitorUnstuck;
            _monitoringUnstuck = false;
        }
    }
    static unsafe bool IsOwnerNode(AtkEventTarget* target, AtkComponentCheckBox* checkbox)
            => target == checkbox->AtkComponentButton.OwnerNode;
}
