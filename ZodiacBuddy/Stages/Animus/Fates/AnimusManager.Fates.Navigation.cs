using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void EnsureFateNavigation(Vector3 destination, bool preferFlight, string reason, FateNavigationIntent intent, float? arrivalDistanceOverride = null)
    {
        var player = Player.Object;
        if (player == null || !_automationRun.IsActive(_fateContext.AutomationRunId))
            return;

        ReleaseFateTacticalMovement();

        if (_fateContext.NavigationActive
            && _fateContext.NavigationIntent == intent
            && _fateContext.NavigationDestination is Vector3 current
            && NavigationGeometry.HorizontalDistanceSquared(current, destination) < FateTravelRepathDistance * FateTravelRepathDistance)
            return;

        if (_fateContext.NavigationActive)
            StopFateOwnedNavigation();

        var distanceSquared = NavigationGeometry.HorizontalDistanceSquared(player.Position, destination);
        var mineDestination = IsUGhamaroMineFateDestination(destination);
        if (mineDestination
            && OuterLaNosceaTravelPolicy.HasCrossedGroundTravelBoundary(player.Position)
            && IsUGhamaroGroundStartReady(player.Position))
            _fateContext.UGhamaroMineGroundTravelActive = true;

        var useMineGroundTravel = ShouldUseUGhamaroGroundFateTravel(destination);
        var shouldMount = distanceSquared >= FateTravelMountDistance * FateTravelMountDistance && (preferFlight || useMineGroundTravel);
        var preserveEntryFlight = Svc.Condition[ConditionFlag.InFlight]
            && (intent is FateNavigationIntent.FateEntry or FateNavigationIntent.FateEntryAnchor);
        var shouldFly = preferFlight
            && !useMineGroundTravel
            && (distanceSquared >= FateTravelMountDistance * FateTravelMountDistance || preserveEntryFlight);
        if (shouldMount && !Svc.Condition[ConditionFlag.Mounted])
        {
            if (Svc.Condition[ConditionFlag.InCombat])
            {
                TargetingHelper.PromoteAggroingEnemy();
                StartFateRotationSolver();
                _fateContext.AutomationStatus = $"Combat is blocking travel to {reason}; clearing incidental aggro before mounting.";
                return;
            }

            TryMountForFateTravel();
            _fateContext.AutomationStatus = useMineGroundTravel
                ? $"Mounting for ground travel through U'Ghamaro toward {reason}."
                : $"Mounting for {reason}.";
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat] && shouldFly)
        {
            TargetingHelper.PromoteAggroingEnemy();
            StartFateRotationSolver();
            _fateContext.AutomationStatus = $"Combat is blocking flight to {reason}; clearing incidental aggro first.";
            return;
        }

        var preserveCombatRotation = (intent is FateNavigationIntent.CombatTarget or FateNavigationIntent.PullCandidate or FateNavigationIntent.ObjectiveAggro or FateNavigationIntent.EscortAnchor or FateNavigationIntent.IncidentalAggro)
            && _fateContext.WorkingFateId != 0
            && GetCurrentFateId() == _fateContext.WorkingFateId;
        if (!preserveCombatRotation)
            RequestFateRotationSolverStop("starting FATE navigation");

        _fateContext.NavigationDestination = destination;
        _fateContext.NavigationIntent = intent;
        _fateContext.NavigationFly = shouldFly && Svc.Condition[ConditionFlag.Mounted];
        _fateContext.NavigationActive = true;
        _fateContext.AutomationState = FateAutomationState.Traveling;
        var arrivalDistance = arrivalDistanceOverride
            ?? (intent is FateNavigationIntent.StarterNpc or FateNavigationIntent.CollectTurnIn ? FateInteractionArrivalDistance : 3f);
        var accepted = StartSimpleMove(destination, _fateContext.NavigationFly, HandleFateNavigationResult, NavigationPurpose.FateTravel, NavigationRestartPolicy.PreserveFlight, ResolveFateNavigationDestination, arrivalDistance: arrivalDistance);
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Navigation intent={intent} destination={destination} fly={_fateContext.NavigationFly} mineGround={useMineGroundTravel} accepted={accepted} reason={reason}.");
        if (!accepted)
        {
            _fateContext.NavigationActive = false;
            _fateContext.NavigationDestination = null;
            _fateContext.NavigationIntent = FateNavigationIntent.None;
        }
    }
    private void EnsureFateLineOfSightRecoveryNavigation(Vector3 destination, string reason, FateNavigationIntent intent)
    {
        if (DateTime.Now < _fateContext.LineOfSightRecoveryRetryAt)
            return;

        EnsureFateNavigation(destination, false, reason, intent, FateLineOfSightRecoveryArrivalDistance);
    }

    private unsafe void TryMountForFateTravel()
    {
        if (Svc.Condition[ConditionFlag.Mounted] || !CanAct || !EzThrottler.Throttle("ZBR_FateMount", 1200))
            return;

        var actionManager = ActionManager.Instance();
        const uint rouletteId = 9;
        if (actionManager->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
            actionManager->UseAction(ActionType.GeneralAction, rouletteId);
    }
    private Vector3? ResolveFateNavigationDestination()
        => _fateContext.NavigationDestination;
    private void HandleFateNavigationResult(AnimusNavigationResult result)
    {
        var completedIntent = _fateContext.NavigationIntent;
        var completedDestination = _fateContext.NavigationDestination;
        _fateContext.NavigationActive = false;
        _fateContext.NavigationDestination = null;
        _fateContext.NavigationIntent = FateNavigationIntent.None;
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();

        if (result == AnimusNavigationResult.Cancelled || !_automationRun.IsActive(_fateContext.AutomationRunId))
            return;

        if (result == AnimusNavigationResult.Arrived
            && _fateCombatEngagement.Phase == FateCombatEngagementPhase.LineOfSightRecovery
            && completedIntent is FateNavigationIntent.CombatTarget or FateNavigationIntent.ObjectiveAggro or FateNavigationIntent.IncidentalAggro)
        {
            _fateContext.LineOfSightRecoveryRetryAt = DateTime.Now.AddMilliseconds(FateLineOfSightRecoveryRetryMs);
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Line-of-sight recovery navigation reached its {FateLineOfSightRecoveryArrivalDistance:F2}y arrival tolerance for {completedIntent}; delaying any blocked-LoS repath by {FateLineOfSightRecoveryRetryMs}ms while visibility is reevaluated.");
        }

        if (result != AnimusNavigationResult.Arrived)
        {
            if (completedIntent == FateNavigationIntent.ResidualAggro)
            {
                _fateContext.CombatTargetId = 0;
                _fateContext.AutomationState = FateAutomationState.ClearingAggro;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Residual cleanup navigation ended with {result}; reassessing.");
                return;
            }

            if (completedIntent == FateNavigationIntent.CollectTurnIn)
            {
                _fateContext.InteractionTargetId = 0;
                _fateContext.AutomationState = FateAutomationState.Resolving;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Collect turn-in navigation ended with {result}; reassessing the live objective NPC.");
                return;
            }

            if (completedIntent == FateNavigationIntent.ReacquireCenter)
            {
                _fateContext.NoCombatTargetSince = DateTime.Now;
                _fateContext.AutomationState = FateAutomationState.Resolving;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] FATE center reacquire navigation ended with {result}; reassessing visible objectives.");
                return;
            }

            if (completedIntent == FateNavigationIntent.PullCandidate)
            {
                ClearExperimentalGrinderPullCandidate();
                _experimentalGrinderNextPullAt = DateTime.Now.AddMilliseconds(600);
                _fateContext.AutomationState = FateAutomationState.Resolving;
                Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Local pull nudge ended with {result}; abandoning that acquisition and reassessing pack pressure without changing RSR target ownership.");
                return;
            }

            if (completedIntent is FateNavigationIntent.EscortAnchor or FateNavigationIntent.DefendAnchor or FateNavigationIntent.CombatTarget or FateNavigationIntent.ObjectiveAggro or FateNavigationIntent.IncidentalAggro)
            {
                if (completedIntent is FateNavigationIntent.CombatTarget or FateNavigationIntent.ObjectiveAggro)
                    _fateContext.CombatTargetId = 0;
                if (completedIntent == FateNavigationIntent.IncidentalAggro)
                    _fateContext.IncidentalAggroTargetId = 0;
                if (completedIntent == FateNavigationIntent.EscortAnchor)
                    _fateContext.EscortFollowRestartAt = DateTime.Now.AddMilliseconds(FateEscortFollowRestartDelayMs);
                _fateContext.AutomationState = FateAutomationState.Resolving;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] {completedIntent} navigation ended with {result}; reassessing.");
                return;
            }

            FailFateAutomation($"FATE navigation ended with {result}.");
            return;
        }

        if (completedIntent == FateNavigationIntent.EscortAnchor)
            _fateContext.EscortFollowRestartAt = DateTime.Now.AddMilliseconds(FateEscortFollowRestartDelayMs);
        if (completedIntent == FateNavigationIntent.ReacquireCenter)
        {
            _fateContext.NoCombatTargetSince = DateTime.Now;
            Service.PluginLog.Verbose("[ZodiacBuddy/FATE] Reached FATE center reacquire destination; rescanning for objective targets.");
        }
        if (completedIntent == FateNavigationIntent.PullCandidate)
        {
            _experimentalGrinderPullProbeUntil = DateTime.Now.AddMilliseconds(GrinderMultiPullProbeSettleMs);
            Service.PluginLog.Verbose(
                $"[ZodiacBuddy/GRINDER-PULL] Reached close pull position for committed candidate {_experimentalGrinderPullCandidateId}; " +
                $"candidate remains committed for a {GrinderMultiPullProbeSettleMs:F0}ms close-probe while RSR stays active. " +
                "Transient RSR target changes will not hand movement back to BMR until the candidate actually joins pressure or the probe expires.");
        }

        _fateContext.AutomationState = completedIntent == FateNavigationIntent.ResidualAggro
            ? FateAutomationState.ClearingAggro
            : FateAutomationState.Resolving;
        if ((completedIntent is FateNavigationIntent.FateEntry or FateNavigationIntent.FateEntryAnchor) && _fateContext.WorkingFateId != 0)
        {
            var currentFateId = GetCurrentFateId();
            if (TryGetFate(_fateContext.WorkingFateId, out var entryFate)
                && entryFate.State == DalamudFateState.Running
                && TryRedirectArrivedFateEntryToAnchor(entryFate, currentFateId == _fateContext.WorkingFateId))
                return;

            if (currentFateId != _fateContext.WorkingFateId)
            {
                _fateContext.EntryArrivalGraceUntil = DateTime.Now.AddMilliseconds(750);
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Arrived at FateId={_fateContext.WorkingFateId} entry destination but current FateId={currentFateId}; allowing registration grace before retrying deeper.");
            }
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            if (completedDestination is Vector3 arrivalAnchor)
            {
                if (_fateContext.LandingRecoveryAnchor is not Vector3 previousAnchor
                    || NavigationGeometry.HorizontalDistanceSquared(previousAnchor, arrivalAnchor) >= 16f)
                    _fateContext.LandingRecoveryAttempts = 0;
                _fateContext.LandingRecoveryAnchor = arrivalAnchor;
            }
            else
            {
                _fateContext.LandingRecoveryAnchor ??= Player.Object?.Position;
            }

            QueueFateDismount();
        }
    }
    private void QueueFateDismount()
    {
        if (!_automationRun.IsActive(_fateContext.AutomationRunId))
            return;
        if (_fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.LandingRecovery)
            return;

        if (_fateContext.DismountQueued)
        {
            if (DateTime.Now < _fateContext.DismountDeadline
                || _restartNavigation != null
                || VNavmesh.Path.IsRunning()
                || VNavmesh.Nav.PathfindInProgress())
                return;

            Service.PluginLog.Verbose("[ZodiacBuddy/FATE] Dismount remained blocked; attempting nearby landing recovery.");
            _fateContext.DismountQueued = false;
            RecoverFateLanding();
            return;
        }

        _fateContext.LandingRecoveryAnchor ??= Player.Object?.Position;
        _fateContext.DismountQueued = true;
        _fateContext.DismountDeadline = DateTime.Now.AddMilliseconds(FateDismountRecoveryMs);
        EnqueueDismount();
        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                return false;

            _fateContext.DismountQueued = false;
            _fateContext.DismountDeadline = DateTime.MinValue;
            _fateContext.LandingRecoveryAnchor = null;
            _fateContext.LandingRecoveryAttempts = 0;
            return true;
        }, 2500, "Wait for FATE dismount");
    }
    private void RecoverFateLanding()
    {
        if (_fateContext.LandingRecoveryAttempts >= FateLandingRecoveryMaxAttempts)
        {
            FailFateAutomation($"Could not find a landable position near the FATE arrival point after {FateLandingRecoveryMaxAttempts} recovery attempts.");
            return;
        }

        var player = Player.Object;
        if (player == null)
            return;

        var anchor = _fateContext.LandingRecoveryAnchor ?? player.Position;
        var attempt = _fateContext.LandingRecoveryAttempts++;
        var recovery = ResolveFateLandingRecoveryPoint(player.Position, anchor, attempt);
        if (recovery is not Vector3 destination)
        {
            _fateContext.DismountQueued = true;
            _fateContext.DismountDeadline = DateTime.Now.AddMilliseconds(FateDismountRecoveryMs);
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Landing recovery {attempt + 1}/{FateLandingRecoveryMaxAttempts} found no nearby walkable point.");
            return;
        }

        _fateContext.NavigationDestination = destination;
        _fateContext.NavigationIntent = FateNavigationIntent.LandingRecovery;
        _fateContext.NavigationFly = Svc.Condition[ConditionFlag.Mounted];
        _fateContext.NavigationActive = true;
        _fateContext.AutomationStatus = $"Dismount blocked; moving to nearby landable ground (recovery {attempt + 1}/{FateLandingRecoveryMaxAttempts}).";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Landing recovery {attempt + 1}/{FateLandingRecoveryMaxAttempts} moving to {destination} on the local FATE layer near Y={anchor.Y:F1}.");
        if (StartSimpleMove(destination, _fateContext.NavigationFly, HandleFateLandingRecoveryNavigationResult, NavigationPurpose.FateTravel, NavigationRestartPolicy.PreserveFlight))
            return;

        _fateContext.NavigationActive = false;
        _fateContext.NavigationDestination = null;
        _fateContext.NavigationIntent = FateNavigationIntent.None;
        _fateContext.DismountQueued = true;
        _fateContext.DismountDeadline = DateTime.Now.AddMilliseconds(FateDismountRecoveryMs);
    }
    private void HandleFateLandingRecoveryNavigationResult(AnimusNavigationResult result)
    {
        _fateContext.NavigationActive = false;
        _fateContext.NavigationDestination = null;
        _fateContext.NavigationIntent = FateNavigationIntent.None;
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();

        if (result == AnimusNavigationResult.Cancelled || !_automationRun.IsActive(_fateContext.AutomationRunId))
            return;

        if (result != AnimusNavigationResult.Arrived)
        {
            _fateContext.DismountQueued = true;
            _fateContext.DismountDeadline = DateTime.Now.AddMilliseconds(-FateDismountRecoveryMs);
            _fateContext.AutomationStatus = $"Landing recovery ended with {result}; trying another nearby ground point.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Landing recovery ended with {result}; retrying.");
            return;
        }

        _fateContext.DismountQueued = false;
        _fateContext.DismountDeadline = DateTime.MinValue;
        _fateContext.AutomationStatus = "Reached nearby walkable ground; retrying dismount.";
        Service.PluginLog.Verbose("[ZodiacBuddy/FATE] Landing recovery reached nearby ground; retrying dismount.");
        QueueFateDismount();
    }
    private static Vector3? ResolveFateLandingRecoveryPoint(Vector3 playerPosition, Vector3 landingAnchor, int attempt)
    {
        var recovery = LandingRecoveryResolver.ResolveNearbyGround(
            playerPosition,
            landingAnchor,
            attempt,
            FateLandingRecoveryRadius,
            LandingRecoveryPreference.NearestToPlayer,
            landingAnchor.Y,
            FateLocalLayerVerticalTolerance,
            out var error);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not resolve a FATE landing recovery point: {error.Message}");
        return recovery;
    }
    private void StopFateOwnedNavigation()
    {
        if (!_fateContext.NavigationActive && _restartNavigation?.Purpose != NavigationPurpose.FateTravel)
            return;

        _automationRun.ReplaceNavigation();
        _fateContext.NavigationActive = false;
        _fateContext.NavigationDestination = null;
        _fateContext.NavigationIntent = FateNavigationIntent.None;
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
    }
    private bool TryContinueRegisteredFateEntry(IFate fate)
    {
        if (_fateContext.DismountQueued)
            return false;

        var entryNavigationActive = _fateContext.NavigationActive
            && (_fateContext.NavigationIntent is FateNavigationIntent.FateEntry or FateNavigationIntent.FateEntryAnchor);
        if (TryRedirectFateEntryToAnchor(fate, true))
            return true;

        if (!entryNavigationActive)
            return false;

        if (_fateContext.NavigationIntent == FateNavigationIntent.FateEntryAnchor)
            return true;

        var destination = ResolveFateEntryDestination(fate, false, out _, out _);
        _fateContext.AutomationStatus = $"FATE participation registered; continuing the entry approach briefly while live objectives load in {FateMetadata.GetName(fate.FateId)} ({fate.FateId}).";
        EnsureFateNavigation(destination, true, $"live FATE {fate.FateId}", FateNavigationIntent.FateEntry);
        return true;
    }

    private bool TryRefreshActiveFateEntryAnchor(IFate fate)
    {
        if (!_fateContext.NavigationActive
            || _fateContext.NavigationIntent is not (FateNavigationIntent.FateEntry or FateNavigationIntent.FateEntryAnchor))
            return false;

        return TryRedirectFateEntryToAnchor(fate, GetCurrentFateId() == fate.FateId);
    }

    private bool TryRedirectArrivedFateEntryToAnchor(IFate fate, bool participationRegistered)
        => Svc.Condition[ConditionFlag.Mounted]
            && TryRedirectFateEntryToAnchor(fate, participationRegistered);

    private bool TryRedirectFateEntryToAnchor(IFate fate, bool participationRegistered)
    {
        var destination = ResolveFateEntryDestination(fate, false, out var entryIntent, out var anchorDescription);
        if (entryIntent != FateNavigationIntent.FateEntryAnchor)
            return false;

        var player = Player.Object;
        if (player != null
            && NavigationGeometry.HorizontalDistanceSquared(player.Position, destination) <= FateEntryAnchorArrivalDistance * FateEntryAnchorArrivalDistance)
            return false;

        _fateContext.AutomationStatus = participationRegistered
            ? $"FATE participation registered; continuing toward {anchorDescription} before landing in {FateMetadata.GetName(fate.FateId)} ({fate.FateId})."
            : $"Live objective loaded; redirecting entry toward {anchorDescription} in {FateMetadata.GetName(fate.FateId)} ({fate.FateId}).";
        EnsureFateNavigation(destination, true, $"{anchorDescription} in live FATE {fate.FateId}", FateNavigationIntent.FateEntryAnchor);
        return true;
    }

    private Vector3 ResolveFateEntryDestination(IFate fate, bool forceCenter, out FateNavigationIntent intent, out string anchorDescription)
    {
        var player = Player.Object;
        var center = fate.Position;
        intent = FateNavigationIntent.FateEntry;
        anchorDescription = string.Empty;

        if (forceCenter)
        {
            _fateContext.EntryAnchorId = 0;
            _fateContext.EntryAnchorDestination = null;
            return ResolveReachableFateDestination(center, Math.Min(4f, Math.Max(2f, fate.Radius * 0.1f)), center.Y) ?? center;
        }

        if (player != null && TryResolveFateEntryAnchor(fate, out var anchor, out var source))
        {
            var anchorDestination = _fateContext.EntryAnchorDestination;
            if (anchorDestination == null)
            {
                anchorDestination = ResolveFateEntryAnchorDestination(fate, anchor, player.Position);
                if (anchorDestination is Vector3 pinnedDestination)
                {
                    _fateContext.EntryAnchorDestination = pinnedDestination;
                    var anchorDistance = MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(anchor.Position, pinnedDestination));
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Entry anchor destination pinned for FateId={fate.FateId}: anchor='{anchor.Name.TextValue}' id={anchor.GameObjectId} destination={pinnedDestination} horizontalFromAnchor={anchorDistance:F1}y hitbox={anchor.HitboxRadius:F1}y.");
                }
            }

            if (anchorDestination is Vector3 destination)
            {
                intent = FateNavigationIntent.FateEntryAnchor;
                anchorDescription = $"{source} '{anchor.Name.TextValue}'";
                return destination;
            }
        }

        if (player == null)
            return ResolveReachableFateDestination(center, fate.Radius, center.Y) ?? center;

        var horizontal = new Vector2(player.Position.X - center.X, player.Position.Z - center.Z);
        if (horizontal.LengthSquared() <= 0.001f)
            return ResolveReachableFateDestination(center, fate.Radius, center.Y) ?? center;

        var entryRadius = Math.Max(3f, fate.Radius * FateInitialEntryRadiusFraction);
        horizontal = Vector2.Normalize(horizontal) * entryRadius;
        var candidate = new Vector3(center.X + horizontal.X, center.Y, center.Z + horizontal.Y);
        return ResolveReachableFateDestination(candidate, Math.Min(8f, Math.Max(6f, fate.Radius * 0.2f)), center.Y)
            ?? ResolveReachableFateDestination(center, fate.Radius, center.Y)
            ?? center;
    }

    private bool TryResolveFateEntryAnchor(IFate fate, out IGameObject anchor, out string source)
    {
        anchor = default!;
        source = string.Empty;
        var player = Player.Object;
        if (player == null)
            return false;

        var discoveryRadius = fate.Radius + FateEntryAnchorDiscoveryPadding;
        if (NavigationGeometry.HorizontalDistanceSquared(player.Position, fate.Position) > discoveryRadius * discoveryRadius)
            return false;

        IGameObject? resolved = null;
        if (IsCollectFate(fate.FateId))
        {
            if (TryGetStickyCollectObjective(fate.FateId, out var stickyCollectObjective))
            {
                resolved = stickyCollectObjective;
                source = "sticky Collect objective";
            }
            else
            {
                resolved = FateTargeting.GetNearestEventObject(fate.FateId);
                if (resolved != null)
                    source = "Collect objective";
                else if (TryGetStickyEntryEnemy(fate.FateId, out var stickyCollectEnemy))
                {
                    resolved = stickyCollectEnemy;
                    source = "sticky Collect acquisition target";
                }
                else
                {
                    resolved = FateTargeting.GetNearestCombatTarget(fate.FateId, 0);
                    if (resolved != null)
                        source = "Collect acquisition target";
                }
            }
        }
        else if (IsDefendFate(fate.FateId))
        {
            resolved = ResolveFateDefendAnchor(fate.FateId, _fateContext.EntryAnchorId, out source);
            if (resolved == null && TryGetStickyEntryEnemy(fate.FateId, out var stickyDefendEnemy))
            {
                resolved = stickyDefendEnemy;
                source = "sticky Defend combat target";
            }
            if (resolved == null)
            {
                resolved = FateTargeting.GetBestCombatTarget(fate.FateId, 0);
                if (resolved != null)
                    source = "Defend combat target";
            }
        }
        else if (IsEscortFate(fate.FateId))
        {
            resolved = ResolveFateProtectedAnchor(fate.FateId, _fateContext.EntryAnchorId, out source);
            if (resolved == null && TryGetStickyEntryEnemy(fate.FateId, out var stickyEscortEnemy))
            {
                resolved = stickyEscortEnemy;
                source = "sticky Escort combat target";
            }
            if (resolved == null)
            {
                resolved = FateTargeting.GetBestCombatTarget(fate.FateId, 0);
                if (resolved != null)
                    source = "Escort combat target";
            }
        }
        else
        {
            var preferred = GetPreferredFateCombatTarget(fate.FateId, _fateContext.EntryAnchorId);
            if (preferred != null)
            {
                resolved = preferred;
                source = "priority combat target";
            }
            else if (TryGetStickyEntryEnemy(fate.FateId, out var stickyEnemy))
            {
                resolved = stickyEnemy;
                source = "sticky combat target";
            }
            else
            {
                resolved = FateTargeting.GetBestCombatTarget(fate.FateId, 0);
                if (resolved != null)
                    source = "combat target";
            }
        }

        if (resolved == null)
        {
            if (_fateContext.EntryAnchorId != 0)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Entry anchor invalidated for FateId={fate.FateId}; clearing the pinned landing destination.");
                _fateContext.EntryAnchorId = 0;
                _fateContext.EntryAnchorDestination = null;
            }
            return false;
        }

        if (_fateContext.EntryAnchorId != resolved.GameObjectId)
        {
            _fateContext.EntryAnchorId = resolved.GameObjectId;
            _fateContext.EntryAnchorDestination = null;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Entry anchor selected for FateId={fate.FateId}: {source} '{resolved.Name.TextValue}' id={resolved.GameObjectId} position={resolved.Position}.");
        }

        anchor = resolved;
        return true;
    }

    private bool TryGetStickyCollectObjective(ushort fateId, out IGameObject objective)
    {
        if (_fateContext.EntryAnchorId != 0
            && FateTargeting.TryGetObject(_fateContext.EntryAnchorId, out var obj)
            && obj is not IBattleNpc
            && obj.IsTargetable
            && FateTargeting.GetFateId(obj) == fateId)
        {
            objective = obj;
            return true;
        }

        objective = default!;
        return false;
    }

    private bool TryGetStickyEntryEnemy(ushort fateId, out IBattleNpc enemy)
    {
        if (_fateContext.EntryAnchorId != 0
            && FateTargeting.TryGetObject(_fateContext.EntryAnchorId, out var obj)
            && obj is IBattleNpc npc
            && FateTargeting.IsFateEnemy(npc, fateId))
        {
            enemy = npc;
            return true;
        }

        enemy = default!;
        return false;
    }

    private static Vector3? ResolveFateEntryAnchorDestination(IFate fate, IGameObject anchor, Vector3 playerPosition)
    {
        var incoming = new Vector2(playerPosition.X - anchor.Position.X, playerPosition.Z - anchor.Position.Z);
        if (incoming.LengthSquared() <= 0.001f)
            incoming = new Vector2(fate.Position.X - anchor.Position.X, fate.Position.Z - anchor.Position.Z);
        if (incoming.LengthSquared() <= 0.001f)
            incoming = Vector2.UnitX;

        incoming = Vector2.Normalize(incoming);
        var combatAnchor = anchor is IBattleNpc battleNpc && FateTargeting.IsFateEnemy(battleNpc, fate.FateId);
        var landingDistance = combatAnchor
            ? MathF.Max(0.5f, anchor.HitboxRadius) + FateEntryCombatAnchorClearance
            : FateEntryAnchorLandingDistance;
        var candidate = new Vector3(
            anchor.Position.X + incoming.X * landingDistance,
            anchor.Position.Y,
            anchor.Position.Z + incoming.Y * landingDistance);

        var fromCenter = new Vector2(candidate.X - fate.Position.X, candidate.Z - fate.Position.Z);
        var maxEntryRadius = Math.Max(3f, fate.Radius - FateEntryAnchorInnerMargin);
        if (fromCenter.LengthSquared() > maxEntryRadius * maxEntryRadius)
        {
            fromCenter = Vector2.Normalize(fromCenter) * maxEntryRadius;
            candidate = new Vector3(
                fate.Position.X + fromCenter.X,
                anchor.Position.Y,
                fate.Position.Z + fromCenter.Y);
        }

        Vector3? resolved;
        if (combatAnchor)
        {
            var maximumAnchorDistance = landingDistance + FateEntryCombatAnchorProjectionPadding;
            var floorProbe = new Vector3(candidate.X, anchor.Position.Y + 3f, candidate.Z);
            resolved = NavigationGeometry.ProjectReachableGround(
                floorProbe,
                2f,
                candidate,
                FateEntryCombatAnchorProjectionRadius,
                FateLocalLayerVerticalTolerance,
                out var error,
                anchor.Position.Y,
                FateLocalLayerVerticalTolerance,
                anchor.Position,
                maximumAnchorDistance);
            if (error != null)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not project aggressive combat entry point near '{anchor.Name.TextValue}': {error.Message}");

            if (resolved == null)
            {
                var anchorProbe = new Vector3(anchor.Position.X, anchor.Position.Y + 3f, anchor.Position.Z);
                resolved = NavigationGeometry.ProjectReachableGround(
                    anchorProbe,
                    2f,
                    anchor.Position,
                    maximumAnchorDistance,
                    FateLocalLayerVerticalTolerance,
                    out error,
                    anchor.Position.Y,
                    FateLocalLayerVerticalTolerance,
                    anchor.Position,
                    maximumAnchorDistance);
                if (error != null)
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not project fallback combat entry point near '{anchor.Name.TextValue}': {error.Message}");
            }
        }
        else
        {
            resolved = ResolveReachableFateDestination(candidate, 8f, anchor.Position.Y);
        }

        if (resolved is not Vector3 projected)
            return null;

        var projectedFromCenter = new Vector2(projected.X - fate.Position.X, projected.Z - fate.Position.Z);
        return projectedFromCenter.LengthSquared() <= fate.Radius * fate.Radius ? projected : null;
    }
    private static Vector3? ResolveReachableFateDestination(Vector3 position, float searchRadius, float? preferredLayerY = null)
    {
        var radius = Math.Clamp(searchRadius, 6f, 24f);
        Vector3 floorProbe;
        Vector3 reachableProbe;
        float reachableVerticalHalfExtent;
        float? layerY = null;
        float maxVerticalDelta = float.PositiveInfinity;
        if (preferredLayerY is float preferredY)
        {
            floorProbe = new Vector3(position.X, preferredY + 3f, position.Z);
            reachableProbe = new Vector3(position.X, preferredY, position.Z);
            reachableVerticalHalfExtent = FateLocalLayerVerticalTolerance;
            layerY = preferredY;
            maxVerticalDelta = FateLocalLayerVerticalTolerance;
        }
        else
        {
            floorProbe = new Vector3(position.X, 1024f, position.Z);
            reachableProbe = floorProbe;
            reachableVerticalHalfExtent = 2048f;
        }

        var resolved = NavigationGeometry.ProjectReachableGround(
            floorProbe,
            Math.Min(radius, 8f),
            reachableProbe,
            radius,
            reachableVerticalHalfExtent,
            out var error,
            layerY,
            maxVerticalDelta);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not project FATE destination {position} onto reachable navmesh: {error.Message}");
        return resolved;
    }
}
