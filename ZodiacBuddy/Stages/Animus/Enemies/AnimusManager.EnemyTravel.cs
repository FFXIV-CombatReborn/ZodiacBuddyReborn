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
    private unsafe bool StartEnemyAutomation(EnemyObjectiveDefinition selectedTarget, uint bookId)
    {
        if (bookId == 0 || selectedTarget.MonsterSlot is < 0 or > 9)
            return false;

        var relicNote = RelicNote.Instance();
        if (relicNote == null || relicNote->RelicNoteId != bookId)
            return false;

        var currentProgress = relicNote->GetMonsterProgress(selectedTarget.MonsterSlot);
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Selected {selectedTarget.Name} ({currentProgress}/3).");
        if (currentProgress >= 3)
        {
            Service.Plugin.PrintStepProgress($"{selectedTarget.Name} is already complete in this book.");
            return false;
        }

        ResetRunStateForNewCycle();
        _enemyAutomation.SetTarget(selectedTarget.Name, selectedTarget.MonsterSlot, bookId);

        _preferredInitialEnemyMapLink = _mobSpawnResolver.GetPreferredMapLink(selectedTarget);
        var initialPosition = _preferredInitialEnemyMapLink ?? selectedTarget.Position;
        Service.GameGui.OpenMapWithMapLink(initialPosition);
        if (!Service.Configuration.IsAtmaManagerEnabled)
            return false;

        StartAutomationRun();
        _enemyCompletionReleasePrepared = false;
        _enemyCompletionCombatClearSince = DateTime.MinValue;
        _activeEnemyTarget = selectedTarget;
        _pathingContext = PathingContext.Enemy;
        ResetTeleportCycleFlags();
        _ughamaroMineGroundTravelActive = Service.ClientState.TerritoryType == OuterLaNosceaTravelPolicy.TerritoryTypeId
            && IsUGhamaroMineObjective(selectedTarget)
            && Player.Object is { } player
            && OuterLaNosceaTravelPolicy.HasCrossedGroundTravelBoundary(player.Position)
            && IsUGhamaroGroundStartReady(player.Position);

        if (Service.ClientState.TerritoryType == selectedTarget.Position.TerritoryType.RowId)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Already in {selectedTarget.ZoneName}; starting local travel.");
            EnqueueMountUp();
            return true;
        }

        var aetheryteId = GetNearestAetheryte(initialPosition);
        if (aetheryteId == 0)
        {
            var zoneName = !string.IsNullOrEmpty(selectedTarget.LocationName)
                ? $"{selectedTarget.LocationName}, {selectedTarget.ZoneName}"
                : selectedTarget.ZoneName;
            Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] No aetheryte found for {zoneName}.");
            CancelActiveRun();
            return false;
        }

        BeginTravelTeleport(aetheryteId);
        Service.Plugin.PrintStepProgress($"Teleporting to {selectedTarget.ZoneName} for Enemy: {selectedTarget.Name}.");
        StartTeleportWaiter();
        return true;
    }
    internal unsafe void PrepareCompletedEnemyObjectiveForPlannerRelease()
    {
        if (!_enemyCompletionReleasePrepared)
        {
            _advancedUnstuck.Cancel();
            if (VNavmesh.Path.IsRunning())
                VNavmesh.Path.Stop();
            _automationRun.ClearPendingWork();
            _restartNavigation = null;
            _enemySpawnRelocationQueued = false;
            _enemyTargetFollowRecoveryActive = false;
            StopUnstuckMonitoring();
            _unstuckPhase = UnstuckPhase.Idle;
            _enemyCompletionReleasePrepared = true;
            Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Cleared pending travel after objective completion.");
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            _enemyCompletionCombatClearSince = DateTime.MinValue;
            return;
        }

        if (_enemyCompletionCombatClearSince == DateTime.MinValue)
        {
            _enemyCompletionCombatClearSince = DateTime.Now;
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Combat cleared; settling for {EnemyCompletionCombatSettleMs}ms.");
        }

        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();
        _advancedUnstuck.Cancel();
        _automationRun.ClearPendingWork();
        _restartNavigation = null;
        _enemySpawnRelocationQueued = false;
        _enemyTargetFollowRecoveryActive = false;
        StopUnstuckMonitoring();
        _unstuckPhase = UnstuckPhase.Idle;

        if (Svc.Condition[ConditionFlag.Mounted] && CanAct && EzThrottler.Throttle("ZBR_EnemyCompletionDismount", 1000))
            ActionManager.Instance()->UseAction(ActionType.Mount, 0);
    }
    internal bool IsCompletedEnemyObjectiveReadyForPlannerRelease()
    {
        if (Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.Mounted])
            return false;
        if (_enemyCompletionCombatClearSince == DateTime.MinValue)
            return false;
        return DateTime.Now - _enemyCompletionCombatClearSince >= TimeSpan.FromMilliseconds(EnemyCompletionCombatSettleMs);
    }
    internal bool IsEnemyTargetFollowRecoveryInProgress
        => _enemyTargetFollowRecoveryActive;
    internal bool TryStartTargetNavigation(Vector3 destination, Action<AnimusNavigationResult> completed)
    {
        if (!_automationRun.HasActiveRun)
            return false;

        var targetId = _enemyAutomation.CurrentTargetId;
        if (targetId != 0 && targetId != _enemyTargetFollowRecoveryTargetId)
        {
            _enemyTargetFollowRecoveryTargetId = targetId;
            _enemyTargetFollowFlightRecoveryAttempts = 0;
        }
        if (_enemyTargetFollowRecoveryActive)
            return true;

        var useMineGroundTravel = ShouldUseUGhamaroGroundEnemyTravel();
        if (Svc.Condition[ConditionFlag.Mounted])
        {
            if (!Svc.Condition[ConditionFlag.InCombat] && useMineGroundTravel)
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Target follow using U'Ghamaro ground route.");
                return StartSimpleMove(destination, false, completed, NavigationPurpose.TargetFollow, NavigationRestartPolicy.WalkAfterUnstuck, ResolveTargetDestination, HandleMountedTargetFollowNavigationResult);
            }

            if (!Svc.Condition[ConditionFlag.InCombat] && ShouldFlyMountedEnemyTravel(destination))
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Target follow using flight.");
                return StartSimpleMove(destination, true, completed, NavigationPurpose.TargetFollow, NavigationRestartPolicy.WalkAfterUnstuck, ResolveTargetDestination, HandleMountedTargetFollowNavigationResult);
            }

            return StartSimpleMove(destination, false, completed, NavigationPurpose.TargetFollow, NavigationRestartPolicy.WalkAfterUnstuck, ResolveTargetDestination, HandleMountedTargetFollowNavigationResult);
        }

        if (ShouldQueueTargetMountForEnemyTravel(destination))
            return QueueMountedEnemyNavigation(
                destination,
                completed,
                NavigationPurpose.TargetFollow,
                NavigationRestartPolicy.WalkAfterUnstuck,
                ResolveTargetDestination,
                EnemyTargetMountDistance,
                useMineGroundTravel ? EnemyMountedTravelMode.Ground : EnemyMountedTravelMode.Flight,
                HandleMountedTargetFollowNavigationResult);

        return StartSimpleMove(destination, false, completed, NavigationPurpose.TargetFollow, NavigationRestartPolicy.WalkAfterUnstuck, ResolveTargetDestination, HandleTargetFollowNavigationResult);
    }
    internal bool TryStartNextEnemySpawnNavigation(Action<AnimusNavigationResult> completed)
    {
        if (!_automationRun.HasActiveRun || _activeEnemyTarget is not EnemyObjectiveDefinition)
            return false;
        if (_enemySpawnRelocationQueued || _restartNavigation?.Purpose == NavigationPurpose.EnemySpawnRelocation)
            return true;

        var destination = ResolveNextEnemySpawnDestination();
        if (destination is not Vector3 point)
            return false;

        Action<NavigationRestartDescriptor, AnimusNavigationResult> terminalResultHandler =
            (descriptor, result) => HandleEnemySpawnRelocationResult(descriptor, result, completed);
        var useMineGroundTravel = ShouldUseUGhamaroGroundEnemyTravel();

        if (Svc.Condition[ConditionFlag.Mounted] && !Svc.Condition[ConditionFlag.InCombat])
        {
            if (useMineGroundTravel)
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Spawn relocation using U'Ghamaro ground route.");
                return StartSimpleMove(
                    point,
                    false,
                    completed,
                    NavigationPurpose.EnemySpawnRelocation,
                    NavigationRestartPolicy.WalkAfterUnstuck,
                    ResolveCurrentEnemySpawnDestination,
                    terminalResultHandler);
            }

            Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Spawn relocation using flight.");
            return StartSimpleMove(
                point,
                true,
                completed,
                NavigationPurpose.EnemySpawnRelocation,
                NavigationRestartPolicy.WalkAfterUnstuck,
                ResolveCurrentEnemySpawnDestination,
                terminalResultHandler);
        }

        if (ShouldMountForEnemyTravel(point, EnemyAreaMountDistance))
            return QueueMountedEnemyNavigation(
                point,
                completed,
                NavigationPurpose.EnemySpawnRelocation,
                NavigationRestartPolicy.WalkAfterUnstuck,
                ResolveCurrentEnemySpawnDestination,
                EnemyAreaMountDistance,
                useMineGroundTravel ? EnemyMountedTravelMode.Ground : EnemyMountedTravelMode.Flight,
                terminalResultHandler);

        return StartSimpleMove(
            point,
            false,
            completed,
            NavigationPurpose.EnemySpawnRelocation,
            NavigationRestartPolicy.WalkAfterUnstuck,
            ResolveCurrentEnemySpawnDestination,
            terminalResultHandler);
    }
    private bool TryStartMapFlagNavigation(bool fly, Action<AnimusNavigationResult> completed)
    {
        if (!_automationRun.HasActiveRun)
            return false;

        var destination = ResolveMapFlagDestination();
        if (destination is not Vector3 point)
        {
            completed(AnimusNavigationResult.Rejected);
            return false;
        }

        var purpose = _pathingContext == PathingContext.Leve
            ? NavigationPurpose.LeveMapFlagTravel
            : NavigationPurpose.InitialEnemyMapFlagTravel;
        var restartPolicy = purpose == NavigationPurpose.InitialEnemyMapFlagTravel
            ? NavigationRestartPolicy.WalkAfterUnstuck
            : NavigationRestartPolicy.PreserveFlight;
        Func<Vector3?> destinationResolver = purpose == NavigationPurpose.InitialEnemyMapFlagTravel
            ? ResolveCurrentEnemySpawnDestination
            : ResolveMapFlagDestination;

        if (purpose == NavigationPurpose.InitialEnemyMapFlagTravel && ShouldUseUGhamaroGroundEnemyTravel())
            fly = false;

        if (purpose == NavigationPurpose.InitialEnemyMapFlagTravel && !fly && ShouldMountForEnemyTravel(point, EnemyAreaMountDistance))
            return QueueMountedEnemyNavigation(point, completed, purpose, restartPolicy, destinationResolver, EnemyAreaMountDistance, EnemyMountedTravelMode.Ground);

        return StartSimpleMove(point, fly, completed, purpose, restartPolicy, destinationResolver);
    }
    private Vector3? ResolveMapFlagDestination()
    {
        if (!VNavmesh.Enabled || !VNavmesh.Nav.IsReady())
        {
            Service.PluginLog.Warning("[ZodiacBuddy/ENEMY] Map-flag navigation is unavailable.");
            return null;
        }

        if (_activeEnemyTarget is EnemyObjectiveDefinition)
            return ResolveInitialEnemySpawnDestination();

        try
        {
            var destination = VNavmesh.Query.Mesh.FlagToPoint();
            if (destination.HasValue)
                return destination.Value;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] Could not resolve map flag: {exception.Message}");
            return null;
        }

        Service.PluginLog.Warning("[ZodiacBuddy/ENEMY] Map-flag navigation has no destination.");
        return null;
    }
    private Vector3? ResolveInitialEnemySpawnDestination()
    {
        if (_activeEnemyTarget is not EnemyObjectiveDefinition enemyTarget || Player.Object is not { } player)
            return null;

        if (_lastEnemySpawnDestination.HasValue)
            return _lastEnemySpawnDestination;

        if (_preferredInitialEnemyMapLink is MapLinkPayload preferredMapLink
            && _mobSpawnResolver.TryResolvePreferredWorldDestination(enemyTarget, preferredMapLink, out var preferredSpawnDestination, out _))
        {
            SetCurrentEnemySpawnDestination(preferredSpawnDestination);
            return preferredSpawnDestination;
        }

        if (_mobSpawnResolver.TryResolveWorldDestination(enemyTarget, player.Position, null, out var spawnDestination, out _))
        {
            SetCurrentEnemySpawnDestination(spawnDestination);
            return spawnDestination;
        }

        if (_mobSpawnResolver.TryResolveBookFallback(enemyTarget, out var fallbackDestination))
        {
            SetCurrentEnemySpawnDestination(fallbackDestination);
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Using book-marker fallback for objective {enemyTarget.MonsterNoteTargetId}.");
            return fallbackDestination;
        }

        Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] No reachable spawn destination for objective {enemyTarget.MonsterNoteTargetId}.");
        return null;
    }
    private Vector3? ResolveNextEnemySpawnDestination()
    {
        if (_activeEnemyTarget is not EnemyObjectiveDefinition enemyTarget || Player.Object is not { } player)
            return null;

        if (_mobSpawnResolver.TryResolveWorldDestination(enemyTarget, player.Position, _visitedEnemySpawnDestinations, out var destination, out var candidateCount))
        {
            SetCurrentEnemySpawnDestination(destination);
            return destination;
        }

        var hasBookFallback = _mobSpawnResolver.TryResolveBookFallback(enemyTarget, out var bookFallback);
        if (candidateCount == 1 && hasBookFallback && IsEnemySpawnDestinationAvailable(bookFallback))
        {
            SetCurrentEnemySpawnDestination(bookFallback);
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Using book-marker fallback for objective {enemyTarget.MonsterNoteTargetId}.");
            return bookFallback;
        }

        if ((candidateCount > 1 || (candidateCount == 1 && hasBookFallback)) && _lastEnemySpawnDestination is Vector3 current)
        {
            _visitedEnemySpawnDestinations.Clear();
            _visitedEnemySpawnDestinations.Add(current);
            if (_mobSpawnResolver.TryResolveWorldDestination(enemyTarget, player.Position, _visitedEnemySpawnDestinations, out destination, out candidateCount))
            {
                SetCurrentEnemySpawnDestination(destination);
                return destination;
            }

            if (candidateCount == 1 && hasBookFallback && IsEnemySpawnDestinationAvailable(bookFallback))
            {
                SetCurrentEnemySpawnDestination(bookFallback);
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Using book-marker fallback for objective {enemyTarget.MonsterNoteTargetId}.");
                return bookFallback;
            }
        }

        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] No alternate spawn area for objective {enemyTarget.MonsterNoteTargetId}.");
        return null;
    }
    private bool IsEnemySpawnDestinationAvailable(Vector3 destination)
    {
        var minimumDistanceSquared = MobSpawnResolver.MinimumRelocationDistance * MobSpawnResolver.MinimumRelocationDistance;
        return _visitedEnemySpawnDestinations.All(visited => NavigationGeometry.HorizontalDistanceSquared(visited, destination) >= minimumDistanceSquared);
    }
    private Vector3? ResolveCurrentEnemySpawnDestination()
        => _lastEnemySpawnDestination;
    private void SetCurrentEnemySpawnDestination(Vector3 destination)
    {
        _lastEnemySpawnDestination = destination;
        var minimumDistanceSquared = MobSpawnResolver.MinimumRelocationDistance * MobSpawnResolver.MinimumRelocationDistance;
        if (_visitedEnemySpawnDestinations.Any(visited => NavigationGeometry.HorizontalDistanceSquared(visited, destination) < minimumDistanceSquared))
            return;

        _visitedEnemySpawnDestinations.Add(destination);
    }
    private bool ShouldMountForEnemyTravel(Vector3 destination, float threshold)
    {
        var player = Player.Object;
        if (player == null || Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.Mounted])
            return false;

        return NavigationGeometry.HorizontalDistanceSquared(player.Position, destination) >= threshold * threshold;
    }
    private bool ShouldQueueTargetMountForEnemyTravel(Vector3 destination)
    {
        var player = Player.Object;
        return player != null
            && !Svc.Condition[ConditionFlag.Mounted]
            && NavigationGeometry.HorizontalDistanceSquared(player.Position, destination) >= EnemyTargetMountDistance * EnemyTargetMountDistance;
    }
    private bool ShouldFlyMountedEnemyTravel(Vector3 destination)
    {
        var player = Player.Object;
        return player != null
            && Svc.Condition[ConditionFlag.Mounted]
            && !Svc.Condition[ConditionFlag.InCombat]
            && NavigationGeometry.HorizontalDistanceSquared(player.Position, destination) >= EnemyMountedFlightDistance * EnemyMountedFlightDistance;
    }
    private bool ShouldUseUGhamaroGroundEnemyTravel()
        => _ughamaroMineGroundTravelActive
            && Service.ClientState.TerritoryType == OuterLaNosceaTravelPolicy.TerritoryTypeId
            && _activeEnemyTarget is EnemyObjectiveDefinition target
            && IsUGhamaroMineObjective(target);
    private void LogInitialEnemyMapFlagNavigation(NavigationRestartDescriptor descriptor, EnemyObjectiveDefinition? target, bool accepted)
    {
        if (descriptor.Purpose != NavigationPurpose.InitialEnemyMapFlagTravel || target is not EnemyObjectiveDefinition enemyTarget)
            return;

        var requestKind = descriptor.GroundGuided && descriptor.UnstuckAttempts == 0
            ? "preemptive-guided"
            : _ughamaroMineGroundTravelActive && !descriptor.Fly && descriptor.UnstuckAttempts == 0
                ? "mine-ground"
                : descriptor.UnstuckAttempts == 0 ? "original" : "unstuck-restart";
        var mode = descriptor.GroundGuided ? "ground-guided-flight" : descriptor.Fly ? "flying" : _ughamaroMineGroundTravelActive ? "mounted-ground" : "walking";
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Map-flag request objective={enemyTarget.MonsterNoteTargetId} mode={mode} request={requestKind} accepted={accepted}.");
    }
    private void LogInitialEnemyMapFlagPathStart(NavigationRestartDescriptor? descriptor, int waypointCount)
    {
        if (descriptor is null || descriptor.Purpose != NavigationPurpose.InitialEnemyMapFlagTravel || _activeEnemyTarget is not EnemyObjectiveDefinition target)
            return;

        var requestKind = descriptor.GroundGuided && descriptor.UnstuckAttempts == 0
            ? "preemptive-guided"
            : _ughamaroMineGroundTravelActive && !descriptor.Fly && descriptor.UnstuckAttempts == 0
                ? "mine-ground"
                : descriptor.UnstuckAttempts == 0 ? "original" : "unstuck-restart";
        var mode = descriptor.GroundGuided ? "ground-guided-flight" : descriptor.Fly ? "flying" : _ughamaroMineGroundTravelActive ? "mounted-ground" : "walking";
        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Map-flag path started objective={target.MonsterNoteTargetId} mode={mode} request={requestKind} waypoints={waypointCount}.");
    }
    private Vector3? ResolveTargetDestination()
        => _enemyAutomation.CurrentTargetPosition is FFXVec3 position ? ToSys(position) : null;
}
