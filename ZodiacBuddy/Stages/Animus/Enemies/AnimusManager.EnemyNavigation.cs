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
    private unsafe bool QueueMountedEnemyNavigation(
        Vector3 destination,
        Action<AnimusNavigationResult> completed,
        NavigationPurpose purpose,
        NavigationRestartPolicy restartPolicy,
        Func<Vector3?>? destinationResolver,
        float mountDistance,
        EnemyMountedTravelMode travelMode,
        Action<NavigationRestartDescriptor, AnimusNavigationResult>? terminalResultHandler = null)
    {
        if (!_automationRun.HasActiveRun)
            return false;
        if (_enemySpawnRelocationQueued)
            return true;

        _enemySpawnRelocationQueued = true;
        var runId = _automationRun.ActiveRunId;
        var modeText = travelMode == EnemyMountedTravelMode.Flight ? "flight" : "ground travel";
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Queuing mount for {purpose}; destination is at least {mountDistance:F0} yalms away and will use {modeText}.");

        _automationRun.Enqueue(() =>
        {
            if (!_automationRun.IsActive(runId) || !Svc.Condition[ConditionFlag.InCombat])
                return true;

            _enemyAutomation.BeginIncidentalAggroClearanceForEnemyTravel();
            return false;
        }, 30000, "Clear incidental aggro before enemy travel");

        _automationRun.Enqueue(() =>
        {
            if (!_automationRun.IsActive(runId) || Svc.Condition[ConditionFlag.Mounted])
                return true;
            if (Svc.Condition[ConditionFlag.InCombat])
            {
                _enemyAutomation.BeginIncidentalAggroClearanceForEnemyTravel();
                return false;
            }
            if (!CanAct || !EzThrottler.Throttle("ZBR_EnemyTravelMountRetry", 1000))
                return false;

            var actionManager = ActionManager.Instance();
            const uint rouletteId = 9;
            if (actionManager->GetActionStatus(ActionType.GeneralAction, rouletteId) != 0)
                return false;

            var accepted = actionManager->UseAction(ActionType.GeneralAction, rouletteId);
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Enemy travel mount request accepted={accepted}; waiting for Mounted before advancing.");
            return false;
        }, 30000, "Mount for enemy travel");

        _automationRun.Enqueue(() =>
        {
            if (!_automationRun.IsActive(runId))
                return true;
            if (Svc.Condition[ConditionFlag.InCombat])
            {
                _enemyAutomation.BeginIncidentalAggroClearanceForEnemyTravel();
                return false;
            }
            return Svc.Condition[ConditionFlag.Mounted];
        }, 30000, "Wait for enemy travel mount");

        _automationRun.Enqueue(() =>
        {
            _enemySpawnRelocationQueued = false;
            if (!_automationRun.IsActive(runId))
                return true;

            var resolvedDestination = destinationResolver?.Invoke() ?? destination;
            var effectiveTravelMode = Svc.Condition[ConditionFlag.Mounted]
                ? travelMode
                : EnemyMountedTravelMode.Ground;
            if (effectiveTravelMode != travelMode)
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Mount was not available when {purpose} began; falling back to ground travel for this leg.");

            StartSimpleMove(resolvedDestination, effectiveTravelMode == EnemyMountedTravelMode.Flight, completed, purpose, restartPolicy, destinationResolver, terminalResultHandler);
            return true;
        }, "Start mounted enemy travel");
        return true;
    }
    internal bool TryStartEnemyAggroCleanupNavigation(ulong targetId, Vector3 destination)
    {
        if (!_automationRun.HasActiveRun
            || VNavmesh.Path.IsRunning()
            || VNavmesh.Nav.PathfindInProgress()
            || !IPCSubscriber.IsReady("vnavmesh")
            || !VNavmesh.Nav.IsReady()
            || Svc.Condition[ConditionFlag.BetweenAreas]
            || !EzThrottler.Throttle($"ZBR_EnemyAggroCleanupPath_{targetId}", 5000))
            return false;

        return StartSimpleMove(
            destination,
            false,
            _ => { },
            NavigationPurpose.EnemyAggroCleanup,
            NavigationRestartPolicy.WalkAfterUnstuck);
    }
    internal void StopEnemyAggroCleanupNavigation()
    {
        if (_restartNavigation?.Purpose != NavigationPurpose.EnemyAggroCleanup)
            return;

        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();
        _advancedUnstuck.Cancel();
        _restartNavigation = null;
        StopUnstuckMonitoring();
        _unstuckPhase = UnstuckPhase.Idle;
    }
    private static bool IsUGhamaroMineObjective(EnemyObjectiveDefinition target)
        => target.Position.TerritoryType.RowId == OuterLaNosceaTravelPolicy.TerritoryTypeId
            && OuterLaNosceaTravelPolicy.IsUGhamaroMapLocation(target.Position.XCoord, target.Position.YCoord);
    private bool TryStartEnemyTargetFollowFlightRecovery(NavigationRestartDescriptor descriptor)
    {
        if (_activeEnemyTarget is not EnemyObjectiveDefinition
            || ShouldUseUGhamaroGroundEnemyTravel()
            || _enemyTargetFollowRecoveryActive
            || Svc.Condition[ConditionFlag.InCombat]
            || Player.Object is not { } player
            || !_automationRun.IsActive(descriptor.RunId))
            return false;

        var targetId = _enemyAutomation.CurrentTargetId;
        if (targetId == 0)
            return false;
        if (_enemyTargetFollowRecoveryTargetId != targetId)
        {
            _enemyTargetFollowRecoveryTargetId = targetId;
            _enemyTargetFollowFlightRecoveryAttempts = 0;
        }
        if (_enemyTargetFollowFlightRecoveryAttempts >= EnemyTargetFollowFlightRecoveryMaxAttempts)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery exhausted for target id={targetId} after {EnemyTargetFollowFlightRecoveryMaxAttempts} attempts.");
            return false;
        }

        var targetPosition = descriptor.DestinationResolver?.Invoke() ?? descriptor.Destination;
        Vector3? recovery = null;
        var attempt = 0;
        while (_enemyTargetFollowFlightRecoveryAttempts < EnemyTargetFollowFlightRecoveryMaxAttempts && recovery is not Vector3)
        {
            attempt = _enemyTargetFollowFlightRecoveryAttempts++;
            recovery = LandingRecoveryResolver.ResolveNearbyGround(
                player.Position,
                targetPosition,
                attempt,
                EnemyTargetFollowFlightRecoveryRadius,
                LandingRecoveryPreference.BeyondAnchorFromPlayer,
                out var error);
            if (error != null)
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Could not resolve TargetFollow flight recovery point: {error.Message}");
            if (recovery is not Vector3)
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery {attempt + 1}/{EnemyTargetFollowFlightRecoveryMaxAttempts} found no usable ground point beyond target id={targetId}.");
        }
        if (recovery is not Vector3 stagingPoint)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery could not resolve staging ground for target id={targetId}.");
            return false;
        }

        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
        _enemyTargetFollowRecoveryActive = true;
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow remained stuck after {descriptor.UnstuckAttempts} local recovery attempts; escalating to flight recovery {attempt + 1}/{EnemyTargetFollowFlightRecoveryMaxAttempts} for target id={targetId} via staging point {stagingPoint}.");

        Action<AnimusNavigationResult> completed = result => HandleEnemyTargetFollowFlightRecoveryFallback(descriptor, result);
        Action<NavigationRestartDescriptor, AnimusNavigationResult> terminalResultHandler =
            (recoveryDescriptor, result) => HandleEnemyTargetFollowFlightRecoveryResult(descriptor, recoveryDescriptor, result);

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            if (StartSimpleMove(stagingPoint, true, completed, NavigationPurpose.EnemyTargetFollowRecovery, NavigationRestartPolicy.PreserveFlight, null, terminalResultHandler))
                return true;
        }
        else if (QueueMountedEnemyNavigation(
                     stagingPoint,
                     completed,
                     NavigationPurpose.EnemyTargetFollowRecovery,
                     NavigationRestartPolicy.PreserveFlight,
                     null,
                     MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(player.Position, stagingPoint)),
                     EnemyMountedTravelMode.Flight,
                     terminalResultHandler))
        {
            return true;
        }

        _enemyTargetFollowRecoveryActive = false;
        return false;
    }

    private void HandleEnemyTargetFollowFlightRecoveryFallback(NavigationRestartDescriptor originalDescriptor, AnimusNavigationResult result)
    {
        _enemyTargetFollowRecoveryActive = false;
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery ended with {result} before its terminal handler completed.");
        originalDescriptor.Completed(result);
    }

    private void HandleEnemyTargetFollowFlightRecoveryResult(
        NavigationRestartDescriptor originalDescriptor,
        NavigationRestartDescriptor recoveryDescriptor,
        AnimusNavigationResult result)
    {
        if (result == AnimusNavigationResult.Cancelled
            || !_automationRun.IsActive(recoveryDescriptor.RunId)
            || !ReferenceEquals(_restartNavigation, recoveryDescriptor))
            return;

        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
        if (result != AnimusNavigationResult.Arrived)
        {
            _enemyTargetFollowRecoveryActive = false;
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery ended with {result}; returning control to enemy targeting.");
            originalDescriptor.Completed(result);
            return;
        }

        EnqueueDismount();
        var recoveryTargetId = _enemyTargetFollowRecoveryTargetId;
        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                return false;

            var targetId = _enemyAutomation.CurrentTargetId;
            if (targetId == 0 || targetId != recoveryTargetId)
            {
                _enemyTargetFollowRecoveryActive = false;
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] TargetFollow flight recovery target changed before ground approach resumed; returning control to enemy targeting.");
                originalDescriptor.Completed(AnimusNavigationResult.Rejected);
                return true;
            }

            var targetPosition = ResolveTargetDestination();
            if (targetPosition is not Vector3 destination)
            {
                _enemyTargetFollowRecoveryActive = false;
                originalDescriptor.Completed(AnimusNavigationResult.Rejected);
                return true;
            }

            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery landed beyond target id={targetId}; resuming ground approach to {destination}.");
            _enemyTargetFollowRecoveryActive = false;
            StartSimpleMove(
                destination,
                false,
                originalDescriptor.Completed,
                NavigationPurpose.TargetFollow,
                NavigationRestartPolicy.WalkAfterUnstuck,
                ResolveTargetDestination,
                HandleTargetFollowNavigationResult);
            return true;
        }, 10000, () => HandleEnemyTargetFollowFlightRecoveryResumeTimeout(originalDescriptor, recoveryTargetId), "Resume TargetFollow after flight recovery");
    }

    private void HandleEnemyTargetFollowFlightRecoveryResumeTimeout(NavigationRestartDescriptor originalDescriptor, ulong targetId)
    {
        if (!_automationRun.IsActive(originalDescriptor.RunId) || !_enemyTargetFollowRecoveryActive)
            return;

        _enemyTargetFollowRecoveryActive = false;
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();

        if (_enemyAutomation.CurrentTargetId == 0 || _enemyAutomation.CurrentTargetId != targetId)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery timed out after the target changed from id={targetId}; returning control to enemy targeting.");
            originalDescriptor.Completed(AnimusNavigationResult.Rejected);
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery timed out while combat began for target id={targetId}; returning control to enemy targeting.");
            originalDescriptor.Completed(AnimusNavigationResult.StoppedBeforeArrival);
            return;
        }

        Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] TargetFollow flight recovery {_enemyTargetFollowFlightRecoveryAttempts}/{EnemyTargetFollowFlightRecoveryMaxAttempts} reached its staging point but could not complete landing/dismount for target id={targetId}; trying another recovery point.");
        if (TryStartEnemyTargetFollowFlightRecovery(originalDescriptor))
            return;

        FailEnemyTargetFollowRecovery(targetId, "landing/dismount recovery exhausted");
    }

    private void FailEnemyTargetFollowRecovery(ulong targetId, string reason)
    {
        _enemyTargetFollowRecoveryActive = false;
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
        _enemyAutomationFailed = true;
        Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] TargetFollow recovery failed for target id={targetId}: {reason}. Failing the current enemy objective so book automation can terminate cleanly.");
        _enemyAutomation.CancelAutomationNavigation();
    }

    private void HandleTargetFollowNavigationResult(NavigationRestartDescriptor descriptor, AnimusNavigationResult result)
    {
        if (result == AnimusNavigationResult.Cancelled
            || !_automationRun.IsActive(descriptor.RunId)
            || !ReferenceEquals(_restartNavigation, descriptor))
            return;

        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
        if (result == AnimusNavigationResult.Arrived)
            _enemyTargetFollowFlightRecoveryAttempts = 0;
        descriptor.Completed(result);
    }
    private void HandleMountedTargetFollowNavigationResult(NavigationRestartDescriptor descriptor, AnimusNavigationResult result)
    {
        if (result != AnimusNavigationResult.Arrived || !Svc.Condition[ConditionFlag.Mounted])
        {
            HandleTargetFollowNavigationResult(descriptor, result);
            return;
        }
        if (!_automationRun.IsActive(descriptor.RunId) || !ReferenceEquals(_restartNavigation, descriptor))
            return;

        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
        EnqueueDismount();
        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                return false;
            _enemyTargetFollowFlightRecoveryAttempts = 0;
            descriptor.Completed(AnimusNavigationResult.Arrived);
            return true;
        }, 10000, () =>
        {
            if (!_automationRun.IsActive(descriptor.RunId))
                return;

            Service.PluginLog.Warning("[ZodiacBuddy/ENEMY] Mounted TargetFollow reached its destination but could not complete dismount; returning control to enemy targeting for another pathing attempt.");
            descriptor.Completed(AnimusNavigationResult.StoppedBeforeArrival);
        }, "Wait for target-travel dismount");
    }
    private void HandleEnemySpawnRelocationResult(NavigationRestartDescriptor descriptor, AnimusNavigationResult result, Action<AnimusNavigationResult> completed)
    {
        if (result == AnimusNavigationResult.Cancelled
            || !_automationRun.IsActive(descriptor.RunId)
            || !ReferenceEquals(_restartNavigation, descriptor))
            return;

        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();

        if (result == AnimusNavigationResult.Arrived && Svc.Condition[ConditionFlag.Mounted])
            Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Enemy spawn relocation arrived while mounted; preserving mount for the next search or target-follow leg.");

        completed(result);
    }
    private void CompleteTravelNavigation()
    {

        EnqueueDismount();
        if (_pathingContext == PathingContext.Enemy)
        {
            _enemyDismountRequestedAt = DateTime.Now;
            _enemyLandingRecoveryAnchor ??= _lastEnemySpawnDestination ?? Player.Object?.Position;
        }
        else if (_pathingContext == PathingContext.Leve)
        {
            _leveContext.IssuerDismountRequestedAt = DateTime.Now;
        }

        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
            {
                if (_pathingContext == PathingContext.Enemy
                    && _enemyDismountRequestedAt != DateTime.MinValue
                    && (DateTime.Now - _enemyDismountRequestedAt).TotalMilliseconds >= EnemyDismountRecoveryMs)
                {
                    if (RecoverEnemyLanding())
                        return true;
                }

                if (_pathingContext == PathingContext.Leve
                    && _leveContext.IssuerDismountRequestedAt != DateTime.MinValue
                    && (DateTime.Now - _leveContext.IssuerDismountRequestedAt).TotalMilliseconds >= LeveIssuerDismountRecoveryMs)
                {
                    if (RecoverLeveIssuerLanding())
                        return true;
                }

                if (EzThrottler.Throttle("ZBR_TravelMountedWait", 1000))
                    Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Player still mounted after dismount tasks. Waiting another tick.");
                return false;
            }
            if (VNavmesh.Path.IsRunning())
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Navmesh is still running after dismount tasks. Waiting another tick.");
                return false;
            }
            Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Player dismounted and navmesh idle. Unlocking pathing.");
            if (_pathingContext == PathingContext.Enemy)
            {
                _enemyDismountRequestedAt = DateTime.MinValue;
                _enemyLandingRecoveryAnchor = null;
                _enemyLandingRecoveryAttempts = 0;
                _enemyAutomation.OnAtmaPathingComplete();
                _automationRun.Enqueue(() => { _automationRun.DelayNextImmediate(750); return true; });
                _automationRun.Enqueue(() =>
                {
                    if (VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
                        return true;

                    if (_enemyAutomation.CurrentTargetPosition is FFXVec3 ffxPos)
                        TryStartTargetNavigation(ToSys(ffxPos), HandleFollowupNavigationResult);
                    return true;
                });
            }
            else if (_pathingContext == PathingContext.Leve)
            {
                _leveContext.IssuerDismountRequestedAt = DateTime.MinValue;
                _leveContext.IssuerLandingRecoveryAttempts = 0;
                OnLeveIssuerTravelComplete();
            }
            _pathingContext = PathingContext.None;
            _unstuckPhase = UnstuckPhase.Idle;
            StopUnstuckMonitoring();
            return true;
        }, 60000, "Wait for travel dismount completion");
    }
    private bool RecoverEnemyLanding()
    {
        if (_enemyLandingRecoveryAttempts >= EnemyLandingRecoveryMaxAttempts)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] Could not find a landable position near the enemy arrival point after {EnemyLandingRecoveryMaxAttempts} recovery attempts.");
            _enemyAutomationFailed = true;
            _enemyAutomation.CancelAutomationNavigation();
            return true;
        }

        var player = Player.Object;
        if (player == null)
            return false;

        var anchor = _enemyLandingRecoveryAnchor ?? _lastEnemySpawnDestination ?? player.Position;
        var attempt = _enemyLandingRecoveryAttempts++;
        var recovery = ResolveEnemyLandingRecoveryPoint(player.Position, anchor, attempt);
        if (recovery is not Vector3 destination)
        {
            _enemyDismountRequestedAt = DateTime.Now;
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Landing recovery {attempt + 1}/{EnemyLandingRecoveryMaxAttempts} found no nearby walkable point.");
            return false;
        }

        _enemyDismountRequestedAt = DateTime.Now;
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Landing recovery {attempt + 1}/{EnemyLandingRecoveryMaxAttempts} moving to {destination}.");
        if (StartSimpleMove(destination, true, HandleEnemyLandingRecoveryNavigationResult, NavigationPurpose.EnemyLandingRecovery, NavigationRestartPolicy.PreserveFlight))
            return true;

        _enemyDismountRequestedAt = DateTime.Now.AddMilliseconds(-EnemyDismountRecoveryMs);
        return false;
    }
    private void HandleEnemyLandingRecoveryNavigationResult(AnimusNavigationResult result)
    {
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();

        if (result == AnimusNavigationResult.Cancelled || !_automationRun.HasActiveRun)
            return;

        if (result != AnimusNavigationResult.Arrived)
        {
            _enemyDismountRequestedAt = DateTime.Now.AddMilliseconds(-EnemyDismountRecoveryMs);
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Landing recovery ended with {result}; trying another nearby ground point.");
        }
        else
        {
            _enemyDismountRequestedAt = DateTime.Now;
            Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Landing recovery reached nearby ground; retrying dismount.");
        }

        CompleteTravelNavigation();
    }
    private static Vector3? ResolveEnemyLandingRecoveryPoint(Vector3 playerPosition, Vector3 landingAnchor, int attempt)
    {
        var recovery = LandingRecoveryResolver.ResolveNearbyGround(
            playerPosition,
            landingAnchor,
            attempt,
            EnemyLandingRecoveryRadius,
            LandingRecoveryPreference.NearestToPlayer,
            out var error);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Could not resolve an enemy landing recovery point: {error.Message}");
        return recovery;
    }
}
