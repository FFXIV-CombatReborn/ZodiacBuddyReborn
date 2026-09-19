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
using ZodiacBuddy.Systems.Combat;
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void DriveFateCombat(IFate fate, bool prerequisite)
    {
        if (IsDefendFate(fate.FateId))
        {
            DriveDefendFateCombat(fate);
            return;
        }

        if (IsEscortFate(fate.FateId) && TryRefreshEscortObstacleMap(fate))
            return;

        if (TryDriveFateThreatClear(fate))
            return;

        if (IsCollectFate(fate.FateId))
        {
            DriveCollectFate(fate);
            return;
        }

        if (FateMetadata.PrefersEventObjects(fate.FateId))
        {
            var attacker = FateTargeting.GetBestAggroTarget(fate.FateId, _fateContext.CombatTargetId, fate.Position, fate.Radius);
            if (attacker != null)
            {
                _fateContext.EventObjectSafeAt = DateTime.Now.AddMilliseconds(FateEventObjectCombatGraceMs);
                _fateContext.InteractionTargetId = 0;
                DriveFateObjectiveAggro(fate, attacker);
                return;
            }

            _fateContext.CombatTargetId = 0;
            _fateCombatEngagement.Reset();
            ReleaseFateTacticalMovement();
            if (DateTime.Now < _fateContext.EventObjectSafeAt)
            {
                if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                    RequestFateRotationSolverStop("awaiting objective interaction");
                StopFateOwnedNavigation();
                _fateContext.AutomationStatus = $"Waiting for a clear interaction window in {FateMetadata.GetName(fate.FateId)}.";
                return;
            }

            var eventObject = FateTargeting.GetNearestEventObject(fate.FateId);
            if (eventObject != null)
            {
                DriveFateEventObject(fate, eventObject);
                return;
            }
        }

        var preferredTarget = GetPreferredFateCombatTarget(fate.FateId, _fateContext.CombatTargetId);
        if (preferredTarget != null)
        {
            DriveFateEnemy(fate, preferredTarget);
            return;
        }

        if (IsEscortFate(fate.FateId))
        {
            DriveEscortFateCombat(fate);
            return;
        }

        if (TryDriveExperimentalGrinderCombat(fate))
            return;

        var target = FateTargeting.GetBestCombatTarget(fate.FateId, _fateContext.CombatTargetId);
        if (target != null)
        {
            DriveFateEnemy(fate, target);
            return;
        }

        var fallbackThreat = _fateCombatTargets.SelectFallbackDirectThreat(fate.FateId, fate.Position, fate.Radius);
        if (fallbackThreat != null)
        {
            _fateContext.NoCombatTargetSince = DateTime.MinValue;
            if (_fateContext.CombatTargetId != fallbackThreat.GameObjectId)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Fallback threat '{fallbackThreat.Name.TextValue}' id={fallbackThreat.GameObjectId}; no objective target visible.");
            DriveFateEnemy(fate, fallbackThreat);
            return;
        }

        _fateContext.CombatTargetId = 0;
        _fateCombatEngagement.Reset();
        ReleaseFateTacticalMovement();
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop("no eligible FATE threat");
        }

        if (_fateContext.NoCombatTargetSince == DateTime.MinValue)
        {
            _fateContext.NoCombatTargetSince = DateTime.Now;
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop("no FATE target visible");
            StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Inside {FateMetadata.GetName(fate.FateId)}; waiting briefly for its next objective target.";
            return;
        }

        if ((DateTime.Now - _fateContext.NoCombatTargetSince).TotalMilliseconds < FateCombatReacquireDelayMs)
        {
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop("awaiting FATE target");
            StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Inside {FateMetadata.GetName(fate.FateId)}; waiting briefly for its next objective target.";
            return;
        }

        var centerDestination = ResolveReachableFateDestination(fate.Position, Math.Min(12f, Math.Max(6f, fate.Radius * 0.25f)), fate.Position.Y) ?? fate.Position;
        var centerDistanceSquared = NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, centerDestination);
        if (centerDistanceSquared > FateCombatReacquireCenterTolerance * FateCombatReacquireCenterTolerance)
        {
            EnsureFateNavigation(centerDestination, false, $"FATE center reacquire {fate.FateId}", FateNavigationIntent.ReacquireCenter);
            _fateContext.AutomationStatus = $"No visible objective targets in {FateMetadata.GetName(fate.FateId)}; moving toward the FATE center to reacquire them.";
            return;
        }

        if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
            RequestFateRotationSolverStop("awaiting target near FATE center");
        StopFateOwnedNavigation();
        _fateContext.AutomationStatus = $"Near the center of {FateMetadata.GetName(fate.FateId)}; waiting to reacquire its next objective target.";
    }
    private bool TryDriveFateThreatClear(IFate fate)
    {
        var externalOnly = IsExperimentalGrinderCombatEligibleFate(fate)
            && EnsureExperimentalGrinderPresetReady(out _);
        var threatSelection = _fateCombatTargets.SelectDirectThreat(fate.FateId, fate.Position, fate.Radius, externalOnly);
        var threatScope = threatSelection.ExternalOnly
            ? $", ignoredFate={threatSelection.IgnoredActiveFateThreatCount}"
            : string.Empty;
        if (threatSelection.Entered)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Threat clear entered: fate={fate.FateId}, direct={threatSelection.DirectThreatCount}, buddy={threatSelection.BuddyThreatCount}{threatScope}.");
        else if (threatSelection.Exited)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Threat clear exited: fate={fate.FateId}.");

        if (threatSelection.Target is not IBattleNpc threatTarget)
            return false;

        if (_fateContext.CollectTurnInActive)
        {
            _fateContext.CollectTurnInSafeAt = DateTime.Now.AddMilliseconds(FateEventObjectCombatGraceMs);
            _fateContext.InteractionTargetId = 0;
        }
        if (_experimentalGrinderTargetFreelyActive || _fateRotationSolver.IsAutoDutyOwned)
            ReleaseExperimentalGrinderCombatSession("direct-threat clear", true);
        DriveFateEnemy(fate, threatTarget);
        return true;
    }

    private IBattleNpc? GetPreferredFateCombatTarget(ushort fateId, ulong stickyTargetId)
    {
        var primaryName = FateMetadata.GetPrimaryCombatTargetName(fateId);
        if (primaryName != null)
        {
            var primary = FateTargeting.GetBestCombatTargetByName(fateId, stickyTargetId, primaryName);
            if (primary != null)
                return primary;
        }

        var secondaryName = FateMetadata.GetSecondaryCombatTargetName(fateId);
        return secondaryName == null
            ? null
            : FateTargeting.GetBestCombatTargetByName(fateId, stickyTargetId, secondaryName);
    }
    private IBattleNpc? GetBestFateCombatTarget(IFate fate, ulong stickyTargetId, Vector3? leashCenter = null, float leashDistance = float.MaxValue, ulong defendedObjectId = 0)
    {
        var threatSelection = _fateCombatTargets.SelectDirectThreat(fate.FateId, fate.Position, fate.Radius);
        if (threatSelection.Target is IBattleNpc threat
            && (leashCenter == null || Vector3.DistanceSquared(leashCenter.Value, threat.Position) <= leashDistance * leashDistance))
            return threat;

        var preferred = GetPreferredFateCombatTarget(fate.FateId, stickyTargetId);
        if (preferred != null
            && (leashCenter == null || Vector3.DistanceSquared(leashCenter.Value, preferred.Position) <= leashDistance * leashDistance))
            return preferred;

        return FateTargeting.GetBestCombatTarget(fate.FateId, stickyTargetId, leashCenter, leashDistance, defendedObjectId);
    }
    private unsafe void DriveFateEnemy(IFate fate, IBattleNpc target, FateNavigationIntent navigationIntent = FateNavigationIntent.CombatTarget, string? purpose = null)
    {
        var targetChanged = _fateCombatEngagement.CommitTarget(target.GameObjectId);
        _fateContext.CombatTargetId = target.GameObjectId;
        _fateContext.NoCombatTargetSince = DateTime.MinValue;
        if (FateTargeting.GetFateId(target) == fate.FateId)
            _fateContext.KnownCombatEnemyIds.Add(target.GameObjectId);
        TargetSystem.Instance()->Target = (GameObject*)target.Address;

        var player = Player.Object!;
        var distance = Vector3.Distance(player.Position, target.Position);
        var horizontalDistance = MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(player.Position, target.Position));
        var macroHandoffDistance = GetFateCombatMacroHandoffDistance(target);
        var macroReacquireDistance = macroHandoffDistance + FateCombatMacroReacquireMargin;
        var targetGroundKnown = TryResolveFateCombatTargetGround(target, out var targetGround);
        var verticalDelta = targetGroundKnown ? MathF.Abs(player.Position.Y - targetGround.Y) : 0f;
        var targetLabel = string.IsNullOrWhiteSpace(purpose) ? target.Name.TextValue : $"{purpose} {target.Name.TextValue}";

        if (targetChanged)
        {
            _fateContext.LineOfSightRecoveryRetryAt = DateTime.MinValue;
            var elevation = targetGroundKnown ? $", vertical={verticalDelta:F1}y" : string.Empty;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Target committed: '{target.Name.TextValue}' id={target.GameObjectId}, fate={FateTargeting.GetFateId(target)}, horizontal={horizontalDistance:F1}y{elevation}.");
        }

        var macroThreshold = UsesMacroReacquireThreshold(_fateCombatEngagement.Phase)
            ? macroReacquireDistance
            : macroHandoffDistance;
        if (horizontalDistance > macroThreshold)
        {
            var decision = _fateCombatEngagement.EnterMacroApproach(target.GameObjectId);
            if (decision.PhaseChanged)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {decision.PreviousPhase} -> {decision.Phase}: target={target.GameObjectId}, horizontal={horizontalDistance:F1}y; vnav macro approach.");
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop($"vnav approaching target {target.GameObjectId}");
            EnsureFateNavigation(target.Position, false, $"FATE combat target {target.GameObjectId}", navigationIntent);
            _fateContext.AutomationStatus = $"vnav macro-approaching {targetLabel} in {FateMetadata.GetName(fate.FateId)} ({horizontalDistance:F1}y; BMR handoff at {macroHandoffDistance:F1}y).";
            return;
        }

        var elevationReadyForAcquisition = !targetGroundKnown || verticalDelta <= FateCombatElevationAcquireTolerance;
        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.ObstacleMapRecovery)
        {
            if (!targetGroundKnown)
            {
                StopFateOwnedNavigation();
                _fateCombatEngagement.CompleteObstacleMapRecovery(target.GameObjectId, DateTime.Now);
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle probe lost reachable ground: target={target.GameObjectId}; retaining map.");
            }
            else if (TryCompleteFateObstacleMapRecovery(target, player.Position, horizontalDistance, targetGroundKnown, verticalDelta))
            {
                targetGroundKnown = TryResolveFateCombatTargetGround(target, out targetGround);
                verticalDelta = targetGroundKnown ? MathF.Abs(player.Position.Y - targetGround.Y) : 0f;
                elevationReadyForAcquisition = !targetGroundKnown || verticalDelta <= FateCombatElevationAcquireTolerance;
            }
            else
            {
                ReleaseFateRotationSolverNow($"vnav probing obstacle map for target {target.GameObjectId}");
                ReleaseFateTacticalMovement();
                EnsureFateNavigation(targetGround, false, $"FATE obstacle-map access probe target {target.GameObjectId}", FateNavigationIntent.CombatTarget);
                _fateContext.AutomationStatus = $"vnav probing melee access to {targetLabel} before deciding whether the temporary obstacle map is blocking reachable terrain.";
                return;
            }
        }

        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.LineOfSightRecovery)
        {
            var hasLineOfSight = FateCombatLineOfSight.HasLineOfSight(player.Position, target.Position);
            if (!hasLineOfSight || !elevationReadyForAcquisition)
            {
                ReleaseFateRotationSolverNow($"vnav recovering LoS to target {target.GameObjectId}");
                ReleaseFateTacticalMovement();
                EnsureFateLineOfSightRecoveryNavigation(targetGroundKnown ? targetGround : target.Position, $"FATE line-of-sight recovery target {target.GameObjectId}", navigationIntent);
                _fateContext.AutomationStatus = !elevationReadyForAcquisition
                    ? $"vnav restoring the local terrain layer and line of sight to {targetLabel} in {FateMetadata.GetName(fate.FateId)}."
                    : $"vnav moving around terrain to restore line of sight to {targetLabel} in {FateMetadata.GetName(fate.FateId)}.";
                return;
            }

            _fateContext.LineOfSightRecoveryRetryAt = DateTime.MinValue;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] LoS recovered: target={target.GameObjectId}; handing movement to BossMod.");
            StopFateOwnedNavigation();
        }
        else if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.ElevationRecovery)
        {
            if (!elevationReadyForAcquisition)
            {
                ReleaseFateRotationSolverNow($"vnav recovering elevation to target {target.GameObjectId}");
                ReleaseFateTacticalMovement();
                EnsureFateNavigation(target.Position, false, $"FATE elevation recovery target {target.GameObjectId}", navigationIntent);
                _fateContext.AutomationStatus = $"vnav routing onto the local terrain layer for {targetLabel} in {FateMetadata.GetName(fate.FateId)} (vertical delta {verticalDelta:F1}y).";
                return;
            }

            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Elevation recovered: target={target.GameObjectId}, delta={verticalDelta:F1}y; handing movement to BossMod.");
            StopFateOwnedNavigation();
        }
        else if (!IsTacticalCombatPhase(_fateCombatEngagement.Phase) && !elevationReadyForAcquisition)
        {
            var elevationDecision = _fateCombatEngagement.EnterElevationRecovery(target.GameObjectId);
            if (elevationDecision.PhaseChanged)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {elevationDecision.PreviousPhase} -> {elevationDecision.Phase}: target={target.GameObjectId}, vertical={verticalDelta:F1}y; vnav elevation recovery.");
            ReleaseFateRotationSolverNow($"target {target.GameObjectId} elevation mismatch");
            ReleaseFateTacticalMovement();
            EnsureFateNavigation(target.Position, false, $"FATE elevation recovery target {target.GameObjectId}", navigationIntent);
            _fateContext.AutomationStatus = $"vnav routing onto the local terrain layer for {targetLabel} in {FateMetadata.GetName(fate.FateId)} (vertical delta {verticalDelta:F1}y).";
            return;
        }
        else
        {
            StopFateOwnedNavigation();
        }

        var bossModNavigationTelemetryAvailable = BossModIPC.TryGetNavigationState(out _);
        var now = DateTime.Now;
        var closeRangeClassJob = BossModTacticalMovementLease.IsCloseRangeClassJob(player.ClassJob.RowId);
        var useMeleeCaptureRange = false;
        var meleeCaptureTimedOut = false;
        if (bossModNavigationTelemetryAvailable)
        {
            useMeleeCaptureRange = _fateCombatEngagement.ShouldUseMeleeCaptureRange(
                target.GameObjectId,
                closeRangeClassJob,
                now,
                out meleeCaptureTimedOut);
        }

        if (meleeCaptureTimedOut)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Melee capture timed out: target={target.GameObjectId}; restoring normal BossMod range.");

        if (bossModNavigationTelemetryAvailable && _fateTacticalMovement.Acquire(player.ClassJob.RowId, useMeleeCaptureRange))
        {
            var tacticalRange = _fateTacticalMovement.ConfiguredRange;
            var tacticalGoalRadius = tacticalRange + target.HitboxRadius;
            var tacticalEngageRadius = tacticalGoalRadius + FateTacticalEngageSlack;
            var tacticalRecloseRadius = tacticalGoalRadius + FateTacticalRecloseSlack;

            _fateTerrainRecovery.Tick(_fateTacticalMovement, target.GameObjectId, player.Position);
            if (_fateTerrainRecovery.IsRecovering)
            {
                if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                    RequestFateRotationSolverStop("vnav terrain recovery");
                _fateContext.AutomationStatus = _fateTerrainRecovery.Status;
                return;
            }

            if (_fateTacticalMovement.IsOwned)
            {
                if (!BossModIPC.TryGetNavigationState(out var navigationState))
                {
                    Service.PluginLog.Warning($"[ZodiacBuddy/FATE] BossMod navigation telemetry unavailable: target={target.GameObjectId}; releasing tactical movement for vnav fallback.");
                    ReleaseFateTacticalMovement();
                }
                else
                {
                    var confirmedElevationMismatch = _fateCombatEngagement.ObserveElevation(
                        target.GameObjectId,
                        !targetGroundKnown || verticalDelta <= FateCombatElevationRecoveryTolerance,
                        now);
                    if (confirmedElevationMismatch)
                    {
                        var elevationDecision = _fateCombatEngagement.EnterElevationRecovery(target.GameObjectId);
                        if (elevationDecision.PhaseChanged)
                            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {elevationDecision.PreviousPhase} -> {elevationDecision.Phase}: target={target.GameObjectId}, vertical={verticalDelta:F1}y; vnav elevation recovery.");

                        ReleaseFateRotationSolverNow($"target {target.GameObjectId} elevation changed");
                        ReleaseFateTacticalMovement();
                        EnsureFateNavigation(target.Position, false, $"FATE elevation recovery target {target.GameObjectId}", navigationIntent);
                        _fateContext.AutomationStatus = $"vnav routing back onto the local terrain layer for {targetLabel} in {FateMetadata.GetName(fate.FateId)} (vertical delta {verticalDelta:F1}y).";
                        return;
                    }

                    var decision = _fateCombatEngagement.EvaluateTactical(
                        target.GameObjectId,
                        horizontalDistance,
                        tacticalEngageRadius,
                        tacticalRecloseRadius,
                        navigationState.IsNavigating,
                        navigationState.MovementActive,
                        now);

                    if (decision.PhaseChanged)
                    {
                        var elevation = targetGroundKnown ? $", verticalDelta={verticalDelta:F1}y" : string.Empty;
                        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {decision.PreviousPhase} -> {decision.Phase}: target={target.GameObjectId}, horizontal={horizontalDistance:F1}y{elevation}, BMR navigating={navigationState.IsNavigating}, moving={navigationState.MovementActive}.");
                    }

                    var hasLineOfSight = FateCombatLineOfSight.HasLineOfSight(player.Position, target.Position);
                    var confirmedBlocked = _fateCombatEngagement.ObserveLineOfSight(target.GameObjectId, hasLineOfSight, now);
                    if (!hasLineOfSight)
                    {
                        if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                            RequestFateRotationSolverStop($"LoS blocked to target {target.GameObjectId}");

                        if (confirmedBlocked)
                        {
                            var lineOfSightDecision = _fateCombatEngagement.EnterLineOfSightRecovery(target.GameObjectId);
                            if (lineOfSightDecision.PhaseChanged)
                                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {lineOfSightDecision.PreviousPhase} -> {lineOfSightDecision.Phase}: target={target.GameObjectId}; vnav LoS recovery.");

                            ReleaseFateRotationSolverNow($"vnav recovering LoS to target {target.GameObjectId}");
                            ReleaseFateTacticalMovement();
                            EnsureFateLineOfSightRecoveryNavigation(targetGroundKnown ? targetGround : target.Position, $"FATE line-of-sight recovery target {target.GameObjectId}", navigationIntent);
                            _fateContext.AutomationStatus = $"vnav moving around terrain to restore line of sight to {targetLabel} in {FateMetadata.GetName(fate.FateId)}.";
                        }
                        else
                        {
                            _fateContext.AutomationStatus = $"Terrain blocks line of sight to {targetLabel}; holding RSR briefly before movement recovery.";
                        }

                        return;
                    }

                    var normalMeleeSpacing = closeRangeClassJob && !useMeleeCaptureRange && tacticalRange > 1.5f;
                    var obstacleMapProbeEligible = navigationIntent == FateNavigationIntent.CombatTarget
                        && normalMeleeSpacing
                        && _fateObstacleMaps.HasOwnedTempMap
                        && decision.Phase == FateCombatEngagementPhase.TacticalApproach
                        && horizontalDistance > tacticalEngageRadius
                        && horizontalDistance <= FateObstacleMapMeleeProbeMaxDistance
                        && targetGroundKnown
                        && verticalDelta <= FateCombatElevationAcquireTolerance
                        && hasLineOfSight;
                    if (_fateCombatEngagement.ObserveObstacleMapMeleeStall(target.GameObjectId, obstacleMapProbeEligible, horizontalDistance, now))
                    {
                        var obstacleDecision = _fateCombatEngagement.EnterObstacleMapRecovery(target.GameObjectId, player.Position, now);
                        if (obstacleDecision.PhaseChanged)
                            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {obstacleDecision.PreviousPhase} -> {obstacleDecision.Phase}: target={target.GameObjectId}, horizontal={horizontalDistance:F1}y; vnav obstacle probe.");

                        ReleaseFateRotationSolverNow($"vnav obstacle probe for target {target.GameObjectId}");
                        ReleaseFateTacticalMovement();
                        EnsureFateNavigation(targetGround, false, $"FATE obstacle-map access probe target {target.GameObjectId}", FateNavigationIntent.CombatTarget);
                        _fateContext.AutomationStatus = $"vnav probing melee access to {targetLabel}; the temporary obstacle map will be suppressed only if reachable terrain is proven.";
                        return;
                    }

                    if (!decision.AllowRotation
                        && tacticalRange <= 3f
                        && distance <= tacticalEngageRadius
                        && decision.Phase is FateCombatEngagementPhase.TacticalApproach or FateCombatEngagementPhase.Settling
                        && _fateCombatEngagement.TryClaimEngagementPoke(target.GameObjectId, now))
                    {
                        var pokeResult = TargetSystem.Instance()->InteractWithObject((GameObject*)target.Address, true);
                        var captureReleased = _fateCombatEngagement.RecordEngagementPokeResult(target.GameObjectId, pokeResult != 0);
                        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Melee poke: target={target.GameObjectId}, distance={distance:F1}y, phase={decision.Phase}, result={pokeResult}, captureReleased={captureReleased}.");
                    }

                    if (decision.AllowRotation)
                    {
                        StartFateRotationSolver();
                    }
                    else if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                    {
                        RequestFateRotationSolverStop($"BossMod positioning during {decision.Phase}");
                    }

                    _fateContext.AutomationStatus = decision.Phase switch
                    {
                        FateCombatEngagementPhase.TacticalApproach => $"BossMod locally approaching {targetLabel} in {FateMetadata.GetName(fate.FateId)} ({horizontalDistance:F1}y horizontal, goal {tacticalGoalRadius:F1}y); RSR held.",
                        FateCombatEngagementPhase.Settling => $"BossMod positioning settled near {targetLabel}; waiting briefly for a stable handoff before enabling RSR.",
                        FateCombatEngagementPhase.Engaged => $"Fighting {targetLabel} in {FateMetadata.GetName(fate.FateId)} with BossMod tactical movement.",
                        _ => $"Establishing combat engagement on {targetLabel} in {FateMetadata.GetName(fate.FateId)}.",
                    };
                    return;
                }
            }
        }
        else
        {
            ReleaseFateTacticalMovement();
        }

        var fallbackDecision = _fateCombatEngagement.EnterFallbackApproach(target.GameObjectId);
        if (fallbackDecision.PhaseChanged)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {fallbackDecision.PreviousPhase} -> {fallbackDecision.Phase}: target={target.GameObjectId}; BossMod unavailable, using vnav.");

        if (distance > FateCombatEngageDistance || !elevationReadyForAcquisition)
        {
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop($"vnav fallback to target {target.GameObjectId}");
            EnsureFateNavigation(target.Position, false, $"FATE enemy {target.GameObjectId}", navigationIntent);
            _fateContext.AutomationStatus = !elevationReadyForAcquisition
                ? $"Approaching the local terrain layer for {targetLabel} in {FateMetadata.GetName(fate.FateId)} with vnav fallback (vertical delta {verticalDelta:F1}y)."
                : $"Approaching {targetLabel} in {FateMetadata.GetName(fate.FateId)} with vnav fallback ({distance:F1}y).";
            return;
        }

        StopFateOwnedNavigation();
        var fallbackHasLineOfSight = FateCombatLineOfSight.HasLineOfSight(player.Position, target.Position);
        var fallbackConfirmedBlocked = _fateCombatEngagement.ObserveLineOfSight(target.GameObjectId, fallbackHasLineOfSight, DateTime.Now);
        if (!fallbackHasLineOfSight)
        {
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop($"LoS blocked to fallback target {target.GameObjectId}");

            if (fallbackConfirmedBlocked)
            {
                var lineOfSightDecision = _fateCombatEngagement.EnterLineOfSightRecovery(target.GameObjectId);
                if (lineOfSightDecision.PhaseChanged)
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Phase {lineOfSightDecision.PreviousPhase} -> {lineOfSightDecision.Phase}: target={target.GameObjectId}; vnav LoS recovery.");

                ReleaseFateRotationSolverNow($"vnav recovering LoS to target {target.GameObjectId}");
                EnsureFateLineOfSightRecoveryNavigation(targetGroundKnown ? targetGround : target.Position, $"FATE line-of-sight recovery target {target.GameObjectId}", navigationIntent);
                _fateContext.AutomationStatus = $"vnav moving around terrain to restore line of sight to {targetLabel} in {FateMetadata.GetName(fate.FateId)}.";
            }
            else
            {
                _fateContext.AutomationStatus = $"Terrain blocks line of sight to {targetLabel}; holding RSR briefly before movement recovery.";
            }

            return;
        }

        StartFateRotationSolver();
        _fateContext.AutomationStatus = $"Fighting {targetLabel} in {FateMetadata.GetName(fate.FateId)} without BossMod tactical movement.";
    }

    private bool TryCompleteFateObstacleMapRecovery(IBattleNpc target, Vector3 playerPosition, float horizontalDistance, bool targetGroundKnown, float verticalDelta)
    {
        var localAccessReady = targetGroundKnown
            && verticalDelta <= FateCombatElevationAcquireTolerance
            && FateCombatLineOfSight.HasLineOfSight(playerPosition, target.Position);
        var successDistance = target.HitboxRadius + BossModTacticalMovementLease.CloseRangeDistance + FateTacticalEngageSlack;
        var outcome = _fateCombatEngagement.EvaluateObstacleMapRecovery(
            target.GameObjectId,
            playerPosition,
            horizontalDistance,
            successDistance,
            localAccessReady,
            DateTime.Now,
            out var playerTravel);

        if (outcome is FateObstacleMapProbeOutcome.Inactive or FateObstacleMapProbeOutcome.Active)
            return false;

        StopFateOwnedNavigation();
        if (outcome == FateObstacleMapProbeOutcome.ProvenReachable)
        {
            var suppressed = _fateObstacleMaps.SuppressForWorkingFate($"melee vnav access probe reached {horizontalDistance:F1}y from target {target.GameObjectId} after moving the player {playerTravel:F1}y through terrain BMR would not close across");
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle probe proved reachable: target={target.GameObjectId}, horizontal={horizontalDistance:F1}y, travel={playerTravel:F1}y, mapSuppressed={suppressed}; returning to BossMod.");
        }
        else if (outcome == FateObstacleMapProbeOutcome.ReachedWithoutProof)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle probe inconclusive: target={target.GameObjectId}, horizontal={horizontalDistance:F1}y, travel={playerTravel:F1}y; retaining map.");
        }
        else
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Obstacle probe timed out: target={target.GameObjectId}; retaining map.");
        }

        _fateCombatEngagement.CompleteObstacleMapRecovery(target.GameObjectId, DateTime.Now);
        return true;
    }

    private static float GetFateCombatMacroHandoffDistance(IBattleNpc target)
        => MathF.Max(FateCombatMacroHandoffDistance, target.HitboxRadius + 12f);

    private static bool UsesMacroReacquireThreshold(FateCombatEngagementPhase phase)
        => phase is FateCombatEngagementPhase.TacticalApproach
            or FateCombatEngagementPhase.Settling
            or FateCombatEngagementPhase.Engaged
            or FateCombatEngagementPhase.LineOfSightRecovery
            or FateCombatEngagementPhase.ElevationRecovery
            or FateCombatEngagementPhase.ObstacleMapRecovery;

    private static bool IsTacticalCombatPhase(FateCombatEngagementPhase phase)
        => phase is FateCombatEngagementPhase.TacticalApproach
            or FateCombatEngagementPhase.Settling
            or FateCombatEngagementPhase.Engaged;

    private static bool TryResolveFateCombatTargetGround(IBattleNpc target, out Vector3 ground)
    {
        ground = default;
        try
        {
            var searchHorizontal = MathF.Max(5f, target.HitboxRadius + 3f);
            var resolved = VNavmesh.Query.Mesh.NearestPointReachable(target.Position, searchHorizontal, FateCombatTargetGroundSearchVertical);
            if (resolved is not Vector3 reachable
                || !float.IsFinite(reachable.X)
                || !float.IsFinite(reachable.Y)
                || !float.IsFinite(reachable.Z))
                return false;

            ground = reachable;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFateCombatElevationReadyForAcquisition(Vector3 playerPosition, IBattleNpc target, out float verticalDelta)
    {
        if (!TryResolveFateCombatTargetGround(target, out var targetGround))
        {
            verticalDelta = 0f;
            return true;
        }

        verticalDelta = MathF.Abs(playerPosition.Y - targetGround.Y);
        return verticalDelta <= FateCombatElevationAcquireTolerance;
    }
    private unsafe void DriveFateEventObject(IFate fate, IGameObject eventObject)
    {
        var attacker = FateTargeting.GetBestAggroTarget(fate.FateId, _fateContext.CombatTargetId, fate.Position, fate.Radius);
        if (attacker != null)
        {
            _fateContext.EventObjectSafeAt = DateTime.Now.AddMilliseconds(FateEventObjectCombatGraceMs);
            _fateContext.InteractionTargetId = 0;
            DriveFateObjectiveAggro(fate, attacker);
            return;
        }

        _fateContext.CombatTargetId = 0;
        _fateCombatEngagement.Reset();
        ReleaseFateTacticalMovement();
        if (DateTime.Now < _fateContext.EventObjectSafeAt)
        {
            if (_fateRotationSolver.IsOwned && !_fateRotationSolver.HasPendingStop)
                RequestFateRotationSolverStop("awaiting objective interaction");
            StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Waiting for a clear interaction window in {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        RequestFateRotationSolverStop("FATE objective interaction");
        var distance = Vector3.Distance(Player.Object!.Position, eventObject.Position);
        if (distance > 3f)
        {
            _fateContext.InteractionTargetId = eventObject.GameObjectId;
            EnsureFateNavigation(eventObject.Position, false, $"FATE objective object {eventObject.GameObjectId}", FateNavigationIntent.EventObject);
            _fateContext.AutomationStatus = $"Approaching {eventObject.Name.TextValue} in {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        if (FateTargeting.GetBestAggroTarget(fate.FateId, 0, fate.Position, fate.Radius) is IBattleNpc lastMomentAttacker)
        {
            _fateContext.EventObjectSafeAt = DateTime.Now.AddMilliseconds(FateEventObjectCombatGraceMs);
            _fateContext.InteractionTargetId = 0;
            DriveFateObjectiveAggro(fate, lastMomentAttacker);
            return;
        }

        StopFateOwnedNavigation();
        if (EzThrottler.Throttle($"ZBR_FateObject_{eventObject.GameObjectId}", 900))
        {
            TargetSystem.Instance()->Target = (GameObject*)eventObject.Address;
            TargetSystem.Instance()->InteractWithObject((GameObject*)eventObject.Address, false);
        }
        _fateContext.AutomationStatus = $"Using {eventObject.Name.TextValue} in {FateMetadata.GetName(fate.FateId)}.";
    }
    private unsafe void DriveFateObjectiveAggro(IFate fate, IBattleNpc target)
    {
        if (_fateContext.CombatTargetId != target.GameObjectId)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Objective attacker: '{target.Name.TextValue}' id={target.GameObjectId}, fate={FateTargeting.GetFateId(target)}.");
        DriveFateEnemy(fate, target, FateNavigationIntent.ObjectiveAggro, "objective attacker");
    }
    private static unsafe ushort GetCurrentFateId()
    {
        var manager = FateManager.Instance();
        return manager == null ? (ushort)0 : manager->GetCurrentFateId();
    }
    private unsafe void TryLevelSyncToExactFate(ushort fateId)
    {
        var manager = FateManager.Instance();
        if (manager == null || manager->SyncedFateId == fateId)
            return;

        if (manager->GetCurrentFateId() != fateId)
            return;

        if (EzThrottler.Throttle($"ZBR_FateSync_{fateId}", 1500))
        {
            manager->LevelSync();
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Level sync requested: fate={fateId}.");
        }
    }
}
