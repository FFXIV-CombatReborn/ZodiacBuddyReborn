using Dalamud.Game.ClientState.Conditions;
using System;
using System.Linq;
using System.Numerics;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const float UGhamaroGroundHandoffArrivalDistance = 0.75f;
    private const float UGhamaroGroundHandoffSearchRadius = 8f;
    private const float UGhamaroGroundHandoffVerticalTolerance = 12f;

    private bool TryHandleOuterLaNosceaMineBoundaryTransition(Vector3 currentPosition)
    {
        if (TryStartOuterLaNosceaMineFlightEgress(currentPosition))
            return true;

        return TryStartOuterLaNosceaMineGroundTravel(currentPosition);
    }

    private bool TryStartOuterLaNosceaMineFlightEgress(Vector3 currentPosition)
    {
        var descriptor = _restartNavigation;
        if (descriptor == null
            || Service.ClientState.TerritoryType != OuterLaNosceaTravelPolicy.TerritoryTypeId
            || !Svc.Condition[ConditionFlag.Mounted]
            || Svc.Condition[ConditionFlag.InCombat])
            return false;

        var destination = descriptor.DestinationResolver?.Invoke() ?? descriptor.Destination;
        if (!OuterLaNosceaTravelPolicy.HasExitedGroundTravelBoundary(destination)
            || descriptor.Fly && !descriptor.GroundGuided)
        {
            descriptor.UGhamaroEgressArmed = false;
            return false;
        }

        var enemyScope = descriptor.Purpose is NavigationPurpose.InitialEnemyMapFlagTravel or NavigationPurpose.TargetFollow or NavigationPurpose.EnemySpawnRelocation;
        var fateScope = descriptor.Purpose == NavigationPurpose.FateTravel
            && (_fateContext.NavigationIntent is FateNavigationIntent.Staging or FateNavigationIntent.FateEntry or FateNavigationIntent.FateEntryAnchor or FateNavigationIntent.StarterNpc or FateNavigationIntent.ReacquireCenter);
        if (!enemyScope && !fateScope)
            return false;

        if (!OuterLaNosceaTravelPolicy.HasExitedGroundTravelBoundary(currentPosition))
        {
            if (!descriptor.UGhamaroEgressArmed)
            {
                var owner = fateScope
                    ? $"FateId={_fateContext.WorkingFateId}"
                    : $"enemy objective {(_activeEnemyTarget is EnemyObjectiveDefinition activeEnemy ? activeEnemy.MonsterNoteTargetId : 0)}";
                var mode = descriptor.GroundGuided ? "ground-guided flight recovery" : "mounted-ground navigation";
                Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro {owner} egress armed while inside Z={OuterLaNosceaTravelPolicy.GroundTravelBoundaryZ:F0} at player={currentPosition}; {mode} is heading toward outside destination={destination}.");
            }
            descriptor.UGhamaroEgressArmed = true;
            return false;
        }

        var enemyLatchedEgress = enemyScope
            && _ughamaroMineGroundTravelActive
            && _activeEnemyTarget is EnemyObjectiveDefinition enemyTarget
            && IsUGhamaroMineObjective(enemyTarget);
        var fateLatchedEgress = fateScope && _fateContext.UGhamaroMineGroundTravelActive;
        if (!descriptor.UGhamaroEgressArmed && !enemyLatchedEgress && !fateLatchedEgress)
            return false;

        var previousMode = descriptor.GroundGuided ? "ground-guided flight recovery" : "mine-ground navigation";

        if (enemyScope)
        {
            var objectiveId = _activeEnemyTarget is EnemyObjectiveDefinition activeEnemy ? activeEnemy.MonsterNoteTargetId : 0;
            _ughamaroMineGroundTravelActive = false;
            descriptor.UGhamaroEgressArmed = false;
            ResetUGhamaroGroundHandoffTracking();
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro enemy objective {objectiveId} exited across Z={OuterLaNosceaTravelPolicy.GroundTravelBoundaryZ:F0} at player={currentPosition}; replacing {previousMode} with normal flight toward outside destination={destination}.");
            StartSimpleMove(destination, true, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts, descriptor.ArrivalDistance);
            return true;
        }

        _fateContext.UGhamaroMineGroundTravelActive = false;
        _fateContext.NavigationFly = true;
        descriptor.UGhamaroEgressArmed = false;
        ResetUGhamaroGroundHandoffTracking();
        Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro FateId={_fateContext.WorkingFateId} exited across Z={OuterLaNosceaTravelPolicy.GroundTravelBoundaryZ:F0} at player={currentPosition}; replacing {previousMode} with normal flight toward outside destination={destination}.");
        StartSimpleMove(destination, true, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts, descriptor.ArrivalDistance);
        return true;
    }

    private bool TryStartOuterLaNosceaMineGroundTravel(Vector3 currentPosition)
    {
        var descriptor = _restartNavigation;
        if (descriptor == null
            || Service.ClientState.TerritoryType != OuterLaNosceaTravelPolicy.TerritoryTypeId
            || !OuterLaNosceaTravelPolicy.HasCrossedGroundTravelBoundary(currentPosition))
            return false;

        var destination = descriptor.DestinationResolver?.Invoke() ?? descriptor.Destination;
        if (!OuterLaNosceaTravelPolicy.HasCrossedGroundTravelBoundary(destination))
            return false;

        if (descriptor.Purpose == NavigationPurpose.InitialEnemyMapFlagTravel)
        {
            var enemyTarget = _activeEnemyTarget;
            if (enemyTarget is not EnemyObjectiveDefinition mineEnemyTarget || !IsUGhamaroMineObjective(mineEnemyTarget))
                return false;

            if (!descriptor.Fly && !descriptor.GroundGuided)
            {
                _ughamaroMineGroundTravelActive = true;
                return false;
            }

            return StartUGhamaroGroundHandoff(
                descriptor,
                currentPosition,
                destination,
                $"enemy objective {mineEnemyTarget.MonsterNoteTargetId}",
                () => _ughamaroMineGroundTravelActive = true,
                () => _ughamaroMineGroundTravelActive = false);
        }

        if (descriptor.Purpose != NavigationPurpose.FateTravel || !IsUGhamaroMineFateDestination(destination))
            return false;

        if (!descriptor.Fly && !descriptor.GroundGuided)
        {
            _fateContext.UGhamaroMineGroundTravelActive = true;
            return false;
        }

        return StartUGhamaroGroundHandoff(
            descriptor,
            currentPosition,
            destination,
            $"FateId={_fateContext.WorkingFateId}",
            () =>
            {
                _fateContext.UGhamaroMineGroundTravelActive = true;
                _fateContext.NavigationFly = false;
            },
            () => _fateContext.UGhamaroMineGroundTravelActive = false);
    }

    private void ResetUGhamaroGroundHandoffTracking()
    {
        _ughamaroMineGroundHandoffAttempted = false;
        _ughamaroMineGroundHandoffDeferredLogged = false;
    }

    private bool StartUGhamaroGroundHandoff(
        NavigationRestartDescriptor descriptor,
        Vector3 currentPosition,
        Vector3 destination,
        string owner,
        Action latchGroundTravel,
        Action clearGroundTravel)
    {
        var groundPoint = ResolveUGhamaroGroundHandoff(currentPosition);
        if (groundPoint is not Vector3 handoff)
        {
            if (!_ughamaroMineGroundHandoffDeferredLogged)
            {
                _ughamaroMineGroundHandoffDeferredLogged = true;
                Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro {owner} crossed Z={OuterLaNosceaTravelPolicy.GroundTravelBoundaryZ:F0} at player={currentPosition}, but no nearby reachable ground-mesh handoff was available; retaining flight and retrying the handoff as the route descends.");
            }
            return false;
        }

        var distanceToGround = Vector3.Distance(currentPosition, handoff);
        if (distanceToGround <= UGhamaroGroundHandoffArrivalDistance)
        {
            latchGroundTravel();
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro {owner} reached a valid ground-mesh handoff at player={currentPosition}; switching to mounted ground navigation toward {destination}.");
            StartSimpleMove(destination, false, descriptor.Completed, descriptor.Purpose, descriptor.RestartPolicy, descriptor.DestinationResolver, descriptor.TerminalResultHandler, descriptor.UnstuckAttempts, descriptor.ArrivalDistance);
            return true;
        }

        if (_ughamaroMineGroundHandoffAttempted)
            return false;

        _ughamaroMineGroundHandoffAttempted = true;
        Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro {owner} crossed Z={OuterLaNosceaTravelPolicy.GroundTravelBoundaryZ:F0} at player={currentPosition} while off the ground navmesh; descending mounted flight to reachable handoff={handoff} before ground navigation toward {destination}.");
        StartSimpleMove(
            handoff,
            true,
            result => CompleteUGhamaroGroundHandoff(descriptor, result, owner, latchGroundTravel, clearGroundTravel),
            NavigationPurpose.UGhamaroMineGroundHandoff,
            NavigationRestartPolicy.PreserveFlight,
            null,
            null,
            0,
            UGhamaroGroundHandoffArrivalDistance);
        return true;
    }

    private void CompleteUGhamaroGroundHandoff(
        NavigationRestartDescriptor original,
        AnimusNavigationResult result,
        string owner,
        Action latchGroundTravel,
        Action clearGroundTravel)
    {
        if (!_automationRun.IsActive(original.RunId))
            return;

        var destination = original.DestinationResolver?.Invoke() ?? original.Destination;
        if (result == AnimusNavigationResult.Arrived)
        {
            latchGroundTravel();
            Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] U'Ghamaro {owner} mounted descent reached the ground-mesh handoff; starting mounted ground navigation toward {destination}.");
            StartSimpleMove(destination, false, original.Completed, original.Purpose, original.RestartPolicy, original.DestinationResolver, original.TerminalResultHandler, original.UnstuckAttempts, original.ArrivalDistance);
            return;
        }

        clearGroundTravel();
        Service.PluginLog.Warning($"[ZodiacBuddy/NAV] U'Ghamaro {owner} ground-mesh handoff descent ended with {result}; resuming the original flight path instead of rejecting the objective.");
        StartSimpleMove(destination, true, original.Completed, original.Purpose, original.RestartPolicy, original.DestinationResolver, original.TerminalResultHandler, original.UnstuckAttempts, original.ArrivalDistance);
    }

    private static Vector3? ResolveUGhamaroGroundHandoff(Vector3 playerPosition)
    {
        var floorProbe = new Vector3(playerPosition.X, playerPosition.Y + 3f, playerPosition.Z);
        return NavigationGeometry.ProjectReachableGround(
            floorProbe,
            6f,
            playerPosition,
            UGhamaroGroundHandoffSearchRadius,
            UGhamaroGroundHandoffVerticalTolerance,
            out _,
            playerPosition.Y,
            UGhamaroGroundHandoffVerticalTolerance,
            playerPosition,
            UGhamaroGroundHandoffSearchRadius);
    }

    private bool ShouldUseUGhamaroGroundFateTravel(Vector3 destination)
        => Service.ClientState.TerritoryType == OuterLaNosceaTravelPolicy.TerritoryTypeId
            && IsUGhamaroMineFateDestination(destination)
            && (_fateContext.UGhamaroMineGroundTravelActive
                || Player.Object is { } player
                    && OuterLaNosceaTravelPolicy.HasCrossedGroundTravelBoundary(player.Position)
                    && IsUGhamaroGroundStartReady(player.Position));

    private static bool IsUGhamaroGroundStartReady(Vector3 playerPosition)
        => ResolveUGhamaroGroundHandoff(playerPosition) is Vector3 groundPoint
            && Vector3.Distance(playerPosition, groundPoint) <= UGhamaroGroundHandoffArrivalDistance;

    private bool IsUGhamaroMineFateDestination(Vector3 fallbackDestination)
    {
        var fateId = _fateContext.WorkingFateId != 0 ? _fateContext.WorkingFateId : _fateContext.Request.FateId;
        var fate = Svc.Fates.FirstOrDefault(candidate => candidate.FateId == fateId);
        var policyAnchor = fate != null && fate.Position != Vector3.Zero
            ? fate.Position
            : fallbackDestination;
        return OuterLaNosceaTravelPolicy.IsUGhamaroWorldLocation(policyAnchor);
    }
}
