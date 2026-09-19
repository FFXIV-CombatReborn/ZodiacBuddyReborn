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
    private void TickFateResolver()
    {
        if (_fateContext.DismountQueued && !Svc.Condition[ConditionFlag.Mounted] && !Svc.Condition[ConditionFlag.InFlight])
        {
            _fateContext.DismountQueued = false;
            _fateContext.DismountDeadline = DateTime.MinValue;
            _fateContext.LandingRecoveryAnchor = null;
            _fateContext.LandingRecoveryAttempts = 0;
        }

        if (_fateContext.NavigationActive)
        {
            var targetIdWhileMoving = checked((ushort)_fateContext.Request.FateId);
            if (TryGetFate(targetIdWhileMoving, out var targetWhileMoving)
                && targetWhileMoving.State is DalamudFateState.Running or DalamudFateState.Preparing)
            {
                var targetStarterAvailable = targetWhileMoving.State == DalamudFateState.Preparing
                    && _fateContext.NavigationIntent == FateNavigationIntent.Staging
                    && FateTargeting.FindStartNpc(
                        targetWhileMoving.FateId,
                        _fateContext.NavigationDestination ?? targetWhileMoving.Position,
                        FateMetadata.GetFallbackSpawnerName(targetWhileMoving.FateId)) != null;
                var targetInterruptsNavigation = _fateContext.WorkingFateIsPrerequisite
                    || (targetWhileMoving.State == DalamudFateState.Running
                        && _fateContext.NavigationIntent is FateNavigationIntent.Staging or FateNavigationIntent.StarterNpc)
                    || targetStarterAvailable;
                if (targetInterruptsNavigation)
                {
                    StopFateOwnedNavigation();
                    WorkFate(targetWhileMoving, false);
                    return;
                }

                ObserveFateState(targetWhileMoving, false);
            }

            if (_fateContext.WorkingFateId != 0)
            {
                if (TryGetFate(_fateContext.WorkingFateId, out var movingFate))
                {
                    if (movingFate.State is DalamudFateState.Failed or DalamudFateState.Ended)
                    {
                        StopFateOwnedNavigation();
                        WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                        return;
                    }

                    if (movingFate.State == DalamudFateState.Ending)
                    {
                        StopFateOwnedNavigation();
                        WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                        return;
                    }

                    ObserveFateState(movingFate, _fateContext.WorkingFateIsPrerequisite);
                    if (movingFate.State == DalamudFateState.Running
                        && _fateContext.NavigationIntent is FateNavigationIntent.FateEntry or FateNavigationIntent.FateEntryAnchor
                        && TryRefreshActiveFateEntryAnchor(movingFate))
                        return;

                    var interruptStaging = _fateContext.NavigationIntent == FateNavigationIntent.Staging
                        && (movingFate.State == DalamudFateState.Running
                            || (movingFate.State == DalamudFateState.Preparing
                                && FateTargeting.FindStartNpc(
                                    movingFate.FateId,
                                    _fateContext.NavigationDestination ?? movingFate.Position,
                                    FateMetadata.GetFallbackSpawnerName(movingFate.FateId)) != null));
                    if (interruptStaging
                        || (_fateContext.NavigationIntent == FateNavigationIntent.StarterNpc
                            && movingFate.State == DalamudFateState.Running))
                    {
                        StopFateOwnedNavigation();
                        WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                        return;
                    }

                    if (_fateContext.NavigationIntent == FateNavigationIntent.CombatTarget
                        && movingFate.State == DalamudFateState.Running)
                    {
                        IBattleNpc? target;
                        if (IsEscortFate(movingFate.FateId))
                        {
                            var protectedAnchor = ResolveFateProtectedAnchor(movingFate.FateId, _fateContext.EscortAnchorId, out _);
                            if (protectedAnchor == null
                                || NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, protectedAnchor.Position) > FateEscortHardLeashDistance * FateEscortHardLeashDistance)
                            {
                                _fateContext.CombatTargetId = 0;
                                StopFateOwnedNavigation();
                                WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                                return;
                            }

                            target = GetBestFateCombatTarget(
                                movingFate,
                                _fateContext.CombatTargetId,
                                protectedAnchor.Position,
                                FateEscortCombatLeashDistance,
                                protectedAnchor.GameObjectId);
                        }
                        else if (IsDefendFate(movingFate.FateId))
                        {
                            var defendThreat = FateTargeting.GetBestDefendObjectiveThreat(
                                movingFate.FateId,
                                _fateContext.CombatTargetId,
                                _fateContext.DefendAnchorId,
                                FateDefendObjectiveSwitchHpMargin);
                            target = defendThreat.Target ?? GetBestFateCombatTarget(movingFate, _fateContext.CombatTargetId);
                        }
                        else
                        {
                            target = GetBestFateCombatTarget(movingFate, _fateContext.CombatTargetId);
                        }
                        if (target == null
                            || target.GameObjectId != _fateContext.CombatTargetId
                            || _fateContext.NavigationDestination is not Vector3 combatDestination)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.ObstacleMapRecovery)
                        {
                            var playerPosition = Player.Object!.Position;
                            var horizontalDistance = MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(playerPosition, target.Position));
                            var targetGroundKnown = TryResolveFateCombatTargetGround(target, out var targetGround);
                            var verticalDelta = targetGroundKnown ? MathF.Abs(playerPosition.Y - targetGround.Y) : 0f;
                            if (TryCompleteFateObstacleMapRecovery(target, playerPosition, horizontalDistance, targetGroundKnown, verticalDelta))
                            {
                                WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                                return;
                            }

                            if (targetGroundKnown
                                && NavigationGeometry.HorizontalDistanceSquared(combatDestination, targetGround) >= 4f * 4f)
                            {
                                StopFateOwnedNavigation();
                                WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                                return;
                            }
                        }
                        else if (NavigationGeometry.HorizontalDistanceSquared(combatDestination, target.Position) >= 4f * 4f)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.MacroApproach
                            && NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, target.Position) <= GetFateCombatMacroHandoffDistance(target) * GetFateCombatMacroHandoffDistance(target)
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, target, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.LineOfSightRecovery
                            && FateCombatLineOfSight.HasLineOfSight(Player.Object!.Position, target.Position)
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, target, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.ElevationRecovery
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, target, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.PullCandidate
                        && movingFate.State == DalamudFateState.Running)
                    {
                        var config = Service.Configuration.FateGrinder;
                        var candidate = FindLoadedExperimentalGrinderFateEnemy(_experimentalGrinderPullCandidateId, movingFate.FateId);
                        var currentRsrTarget = GetCurrentExperimentalGrinderTarget(movingFate.FateId);
                        var rsrOutsideFate = Svc.Targets.Target is IBattleNpc invalidRsrTarget
                            && FateTargeting.IsAttackableEnemy(invalidRsrTarget)
                            && !FateTargeting.IsFateEnemy(invalidRsrTarget, movingFate.FateId);
                        var actualThreatIds = FateTargeting.GetCurrentFateThreatIds(movingFate.FateId);
                        var player = Player.Object!;
                        var configuredPackSize = FateGrinderMultiPullProfiles.GetPackSize(config, player.ClassJob.RowId);
                        var desiredPackSize = _experimentalGrinderAdaptivePackCeiling > 0
                            ? Math.Min(configuredPackSize, _experimentalGrinderAdaptivePackCeiling)
                            : configuredPackSize;
                        var candidateEngaged = candidate != null && actualThreatIds.Contains(candidate.GameObjectId);
                        var effectiveEngagedIds = new HashSet<ulong>(actualThreatIds);
                        if (currentRsrTarget != null
                            && (candidate == null || currentRsrTarget.GameObjectId != candidate.GameObjectId || candidateEngaged))
                            effectiveEngagedIds.Add(currentRsrTarget.GameObjectId);
                        var effectiveEngagedCount = effectiveEngagedIds.Count;
                        var now = DateTime.Now;
                        ulong lostThreatId = 0;
                        var lostThreatName = string.Empty;
                        var protectedPressureLost = candidate != null
                            && TryDetectExperimentalGrinderProtectedPressureLoss(
                                movingFate.FateId,
                                actualThreatIds,
                                now,
                                out lostThreatId,
                                out lostThreatName);
                        var retentionReason = string.Empty;
                        var retentionEnvelopeLost = candidate != null
                            && !IsExperimentalGrinderCandidateInsideRetentionEnvelope(
                                candidate,
                                GetLoadedGrinderFateEnemies(movingFate.FateId),
                                effectiveEngagedIds,
                                config.EffectiveMultiPullRadius,
                                config.EffectiveMultiPullRadius + 4f,
                                out retentionReason);
                        var destinationStale = candidate != null
                            && _fateContext.NavigationDestination is Vector3 pullDestination
                            && NavigationGeometry.HorizontalDistanceSquared(pullDestination, candidate.Position) >= 4f * 4f;
                        var candidateOutsideRadius = candidate != null
                            && NavigationGeometry.HorizontalDistanceSquared(player.Position, candidate.Position) > config.EffectiveMultiPullRadius * config.EffectiveMultiPullRadius;
                        var candidateTimedOut = candidate != null
                            && _experimentalGrinderPullCandidateSince != DateTime.MinValue
                            && (now - _experimentalGrinderPullCandidateSince).TotalMilliseconds >= GrinderMultiPullCandidateTimeoutMs;
                        var hpPercent = player.MaxHp == 0 ? 0 : (int)MathF.Round(player.CurrentHp * 100f / player.MaxHp);
                        var hpLow = hpPercent < config.EffectiveMultiPullHpFloorPercent;
                        var forlornNearby = HasNearbyForlorn(GetLoadedGrinderFateEnemies(movingFate.FateId), player.Position);
                        _ = FateTargeting.GetBestDirectThreatTarget(
                            movingFate.FateId,
                            0,
                            movingFate.Position,
                            movingFate.Radius,
                            true,
                            out var externalThreats,
                            out _,
                            out _,
                            out _);

                        if (protectedPressureLost)
                        {
                            var candidateId = candidate!.GameObjectId;
                            var candidateName = candidate.Name.TextValue;
                            var retainedCount = Math.Max(2, actualThreatIds.Count);
                            var previousCeiling = _experimentalGrinderAdaptivePackCeiling > 0
                                ? _experimentalGrinderAdaptivePackCeiling
                                : configuredPackSize;
                            _experimentalGrinderAdaptivePackCeiling = Math.Min(previousCeiling, retainedCount);
                            _experimentalGrinderRetentionHoldUntil = now.AddMilliseconds(GrinderMultiPullRetentionRecoveryMs);
                            RestoreExperimentalGrinderRetainedPackTarget(
                                movingFate.FateId,
                                actualThreatIds,
                                candidateId,
                                "a deliberate pull caused previously engaged pressure to drop");
                            SuppressExperimentalGrinderPullCandidate(candidateId, now);
                            StopFateOwnedNavigation();
                            ClearExperimentalGrinderPullCandidate();
                            SetExperimentalGrinderPullHold(
                                $"pack retention loss detected on {lostThreatName}; adaptive ceiling {_experimentalGrinderAdaptivePackCeiling}/{configuredPackSize}");
                            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Aborted '{candidateName}' ({candidateId}); lost pressure on '{lostThreatName}' ({lostThreatId}), pack ceiling={_experimentalGrinderAdaptivePackCeiling}/{configuredPackSize}.");
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (candidateEngaged)
                        {
                            var candidateName = candidate!.Name.TextValue;
                            var candidateId = candidate.GameObjectId;
                            StopFateOwnedNavigation();
                            ClearExperimentalGrinderPullCandidate();
                            _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                            ClearExperimentalGrinderPullHold();
                            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Acquired '{candidateName}' ({candidateId}), pack={actualThreatIds.Count}/{desiredPackSize}.");
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (retentionEnvelopeLost)
                        {
                            var candidateId = candidate!.GameObjectId;
                            var candidateName = candidate.Name.TextValue;
                            RestoreExperimentalGrinderRetainedPackTarget(
                                movingFate.FateId,
                                actualThreatIds,
                                candidateId,
                                retentionReason);
                            SuppressExperimentalGrinderPullCandidate(candidateId, now);
                            StopFateOwnedNavigation();
                            ClearExperimentalGrinderPullCandidate();
                            _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                            SetExperimentalGrinderPullHold(retentionReason);
                            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Abandoned '{candidateName}' ({candidateId}): {retentionReason}.");
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (!config.MultiPullEnabled
                            || candidate == null
                            || rsrOutsideFate
                            || effectiveEngagedCount >= desiredPackSize
                            || candidateOutsideRadius
                            || candidateTimedOut
                            || hpLow
                            || movingFate.Progress >= GrinderMultiPullStopProgressPercent
                            || forlornNearby
                            || externalThreats > 0)
                        {
                            var reason = !config.MultiPullEnabled
                                ? "multi-pull was disabled"
                                : candidate == null
                                    ? "pull candidate disappeared"
                                    : rsrOutsideFate
                                        ? "RSR hard target left the working FATE"
                                        : effectiveEngagedCount >= desiredPackSize
                                            ? $"desired pack size {desiredPackSize} was reached by other pressure"
                                            : candidateOutsideRadius
                                                ? "pull candidate moved outside the configured radius"
                                                : candidateTimedOut
                                                    ? $"pull candidate did not join pressure within {GrinderMultiPullCandidateTimeoutMs / 1000d:F0}s"
                                                    : hpLow
                                                        ? $"HP fell to {hpPercent}%"
                                                        : movingFate.Progress >= GrinderMultiPullStopProgressPercent
                                                            ? $"FATE progress reached {movingFate.Progress}%"
                                                            : forlornNearby
                                                                ? "a Forlorn priority target appeared"
                                                                : $"external threat pressure={externalThreats}";
                            if (candidateTimedOut && candidate != null)
                                SuppressExperimentalGrinderPullCandidate(candidate.GameObjectId, now);
                            StopFateOwnedNavigation();
                            ClearExperimentalGrinderPullCandidate();
                            _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Pull ended: {reason}.");
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (destinationStale)
                        {
                            StopFateOwnedNavigation();
                            _experimentalGrinderNextPullAt = DateTime.MinValue;
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.ReacquireCenter
                        && movingFate.State == DalamudFateState.Running)
                    {
                        var reacquiredTarget = GetBestFateCombatTarget(movingFate, _fateContext.CombatTargetId);
                        if (reacquiredTarget != null || FateTargeting.GetBestAggroTarget(movingFate.FateId, _fateContext.CombatTargetId, movingFate.Position, movingFate.Radius) != null)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.IncidentalAggro
                        && movingFate.State == DalamudFateState.Running)
                    {
                        Vector3 incidentalCenter;
                        float incidentalLeash;
                        if (IsEscortFate(movingFate.FateId))
                        {
                            var protectedAnchor = ResolveFateProtectedAnchor(movingFate.FateId, _fateContext.EscortAnchorId, out _);
                            if (protectedAnchor == null
                                || NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, protectedAnchor.Position) > FateEscortHardLeashDistance * FateEscortHardLeashDistance)
                            {
                                _fateContext.IncidentalAggroTargetId = 0;
                                StopFateOwnedNavigation();
                                WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                                return;
                            }

                            incidentalCenter = protectedAnchor.Position;
                            incidentalLeash = FateEscortIncidentalAggroLeashDistance;
                        }
                        else if (IsDefendFate(movingFate.FateId))
                        {
                            incidentalCenter = Player.Object!.Position;
                            incidentalLeash = FateDefendIncidentalAggroLeashDistance;
                        }
                        else
                        {
                            _fateContext.IncidentalAggroTargetId = 0;
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        var incidentalTarget = FateTargeting.GetBestIncidentalAggroTarget(
                            movingFate.FateId,
                            _fateContext.IncidentalAggroTargetId,
                            incidentalCenter,
                            incidentalLeash);
                        if (incidentalTarget == null
                            || _fateContext.NavigationDestination is not Vector3 incidentalDestination
                            || NavigationGeometry.HorizontalDistanceSquared(incidentalDestination, incidentalTarget.Position) >= 4f * 4f)
                        {
                            _fateContext.IncidentalAggroTargetId = 0;
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.LineOfSightRecovery
                            && FateCombatLineOfSight.HasLineOfSight(Player.Object!.Position, incidentalTarget.Position)
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, incidentalTarget, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.ElevationRecovery
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, incidentalTarget, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.ObjectiveAggro
                        && movingFate.State == DalamudFateState.Running)
                    {
                        var objectiveAttacker = FateTargeting.GetBestAggroTarget(movingFate.FateId, _fateContext.CombatTargetId, movingFate.Position, movingFate.Radius);
                        if (objectiveAttacker == null
                            || _fateContext.NavigationDestination is not Vector3 objectiveAggroDestination
                            || NavigationGeometry.HorizontalDistanceSquared(objectiveAggroDestination, objectiveAttacker.Position) >= 4f * 4f)
                        {
                            _fateContext.CombatTargetId = 0;
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.MacroApproach
                            && NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, objectiveAttacker.Position) <= GetFateCombatMacroHandoffDistance(objectiveAttacker) * GetFateCombatMacroHandoffDistance(objectiveAttacker)
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, objectiveAttacker, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.LineOfSightRecovery
                            && FateCombatLineOfSight.HasLineOfSight(Player.Object!.Position, objectiveAttacker.Position)
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, objectiveAttacker, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateCombatEngagement.Phase == FateCombatEngagementPhase.ElevationRecovery
                            && IsFateCombatElevationReadyForAcquisition(Player.Object!.Position, objectiveAttacker, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.StarterNpc
                        && !FateTargeting.TryGetObject(_fateContext.InteractionTargetId, out _))
                    {
                        StopFateOwnedNavigation();
                        WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                        return;
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.CollectTurnIn
                        && movingFate.State is DalamudFateState.Running or DalamudFateState.Ending)
                    {
                        var objectiveNpc = ResolveCollectObjectiveNpc(movingFate.FateId);
                        var objectiveLost = _fateContext.InteractionTargetId != 0 && objectiveNpc == null;
                        var objectiveReached = objectiveNpc != null
                            && Vector3.Distance(Player.Object!.Position, objectiveNpc.Position) <= FateInteractDistance;
                        var objectiveMoved = objectiveNpc != null
                            && (_fateContext.NavigationDestination is not Vector3 turnInDestination
                                || NavigationGeometry.HorizontalDistanceSquared(turnInDestination, objectiveNpc.Position) >= FateTravelRepathDistance * FateTravelRepathDistance);
                        if (objectiveLost
                            || objectiveReached
                            || objectiveMoved
                            || FateTargeting.GetBestAggroTarget(movingFate.FateId, _fateContext.CombatTargetId, movingFate.Position, movingFate.Radius) != null)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.EventObject)
                    {
                        if (!FateTargeting.TryGetObject(_fateContext.InteractionTargetId, out _))
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (movingFate.State == DalamudFateState.Running
                            && FateTargeting.GetBestAggroTarget(movingFate.FateId, _fateContext.CombatTargetId, movingFate.Position, movingFate.Radius) != null)
                        {
                            _fateContext.EventObjectSafeAt = DateTime.Now.AddMilliseconds(FateEventObjectCombatGraceMs);
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.EscortAnchor
                        && movingFate.State == DalamudFateState.Running)
                    {
                        var friendlyAnchor = ResolveFateProtectedAnchor(movingFate.FateId, _fateContext.EscortAnchorId, out _);
                        if (friendlyAnchor == null)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        var liveAnchor = ResolveReachableProtectedAnchorDestination(friendlyAnchor.Position) ?? friendlyAnchor.Position;
                        if (NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, friendlyAnchor.Position) <= FateEscortFollowStopDistance * FateEscortFollowStopDistance)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateContext.NavigationDestination is not Vector3 escortDestination
                            || NavigationGeometry.HorizontalDistanceSquared(escortDestination, liveAnchor) >= FateEscortRepathDistance * FateEscortRepathDistance)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                    else if (_fateContext.NavigationIntent == FateNavigationIntent.DefendAnchor
                        && movingFate.State == DalamudFateState.Running)
                    {
                        var defendThreat = FateTargeting.GetBestDefendObjectiveThreat(
                            movingFate.FateId,
                            _fateContext.CombatTargetId,
                            _fateContext.DefendAnchorId,
                            FateDefendObjectiveSwitchHpMargin);
                        if (defendThreat.Target != null)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        var defendAnchor = ResolveFateDefendAnchor(movingFate.FateId, _fateContext.DefendAnchorId, out _);
                        if (defendAnchor == null)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        var liveAnchor = ResolveReachableProtectedAnchorDestination(defendAnchor.Position) ?? defendAnchor.Position;
                        if (NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, defendAnchor.Position) <= FateDefendReturnStopDistance * FateDefendReturnStopDistance)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }

                        if (_fateContext.NavigationDestination is not Vector3 defendDestination
                            || NavigationGeometry.HorizontalDistanceSquared(defendDestination, liveAnchor) >= FateDefendRepathDistance * FateDefendRepathDistance)
                        {
                            StopFateOwnedNavigation();
                            WorkFate(movingFate, _fateContext.WorkingFateIsPrerequisite);
                            return;
                        }
                    }
                }
                else if (TryHandlePreparingBookFateGone(_fateContext.WorkingFateId, _fateContext.WorkingFateIsPrerequisite))
                {
                    return;
                }
                else if (_fateContext.WorkingFateObservedRunning)
                {
                    StopFateOwnedNavigation();
                    HandleObservedFateGone(_fateContext.WorkingFateIsPrerequisite);
                    return;
                }
            }
            return;
        }

        var targetId = checked((ushort)_fateContext.Request.FateId);
        if (TryGetFate(targetId, out var targetFate))
        {
            WorkFate(targetFate, false);
            return;
        }

        if (TryHandlePreparingBookFateGone(targetId, false))
            return;

        if (_fateContext.TargetObservedRunning)
        {
            HandleObservedFateGone(false);
            return;
        }

        var prerequisiteId = FateMetadata.GetPrerequisite(targetId);
        if (prerequisiteId != 0 && !_fateContext.PrerequisiteDone)
        {
            if (TryGetFate(prerequisiteId, out var prerequisiteFate))
            {
                WorkFate(prerequisiteFate, true);
                return;
            }

            if (TryHandlePreparingBookFateGone(prerequisiteId, true))
                return;

            if (_fateContext.WorkingFateIsPrerequisite && _fateContext.WorkingFateId == prerequisiteId && _fateContext.WorkingFateObservedRunning)
            {
                HandleObservedFateGone(true);
                return;
            }

            StageForAbsentFate(prerequisiteId, true);
            return;
        }

        StageForAbsentFate(targetId, false);
    }
    private void WorkFate(IFate fate, bool prerequisite)
    {
        SetWorkingFate(fate.FateId, prerequisite);
        ObserveFateState(fate, prerequisite);

        if (fate.State == DalamudFateState.Failed)
        {
            if (prerequisite)
            {
                _fateContext.WorkingFateObservedPreparing = false;
                _fateContext.WorkingFateObservedRunning = false;
                _fateContext.WorkingFateCompletionObserved = false;
                _fateContext.WorkingFateMissingSince = DateTime.MinValue;
                _fateObstacleMaps.Reset();
                _fateContext.WorkingFateId = 0;
                _fateContext.AutomationStatus = $"Prerequisite {FateMetadata.GetName(fate.FateId)} failed; waiting for another attempt.";
                return;
            }

            RetryFateLater(_fateContext.HasBookTarget
                ? $"{_fateContext.Request.Name} failed before the book could credit it."
                : $"{_fateContext.Request.Name} ended in the Failed state.");
            return;
        }

        if (fate.State == DalamudFateState.Ending
            && IsCollectFate(fate.FateId)
            && (GetCollectEventItemCount(fate.FateId) > 0 || _fateContext.CollectTurnInActive))
        {
            _fateContext.AutomationState = FateAutomationState.Fighting;
            DriveFateCombat(fate, prerequisite);
            return;
        }

        if (fate.State == DalamudFateState.Ending)
        {
            StopFateOwnedNavigation();
            FateTargeting.TrackVisibleFateEnemyIds(fate.FateId, _fateContext.KnownCombatEnemyIds);
            var stillTagged = FateTargeting.CountKnownEnemiesStillTagged(fate.FateId, _fateContext.KnownCombatEnemyIds);
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Residual cleanup started for {fate.FateId}, tracked={_fateContext.KnownCombatEnemyIds.Count}, tagged={stillTagged}.");
            _fateContext.CombatTargetId = 0;
            _fateContext.IncidentalAggroTargetId = 0;
            _fateContext.ResidualAggroOrigin ??= Player.Object?.Position;
            _fateContext.ResidualLoggedTargetId = 0;
            _fateContext.PostCombatOutcome = prerequisite
                ? (_fateContext.WorkingFateCompletionObserved ? FatePostCombatOutcome.PrerequisiteComplete : FatePostCombatOutcome.PrerequisiteRetry)
                : (!_fateContext.HasBookTarget && !_fateContext.WorkingFateCompletionObserved ? FatePostCombatOutcome.TargetFailed : FatePostCombatOutcome.TargetComplete);
            _fateContext.AutomationState = FateAutomationState.ClearingAggro;
            _fateContext.AutomationStatus = Svc.Condition[ConditionFlag.InCombat]
                ? $"{FateMetadata.GetName(fate.FateId)} is ending; clearing residual aggro without pursuing departing FATE mobs."
                : $"{FateMetadata.GetName(fate.FateId)} is ending; waiting for the game to finalize it.";
            return;
        }

        if (fate.State == DalamudFateState.Ended)
        {
            if (_fateContext.WorkingFateObservedRunning)
                HandleObservedFateGone(prerequisite);
            return;
        }

        if (fate.State == DalamudFateState.Preparing)
        {
            if (_fateContext.Request.IsFiller)
            {
                if (_fateContext.FillerStartDeadline == DateTime.MinValue)
                    _fateContext.FillerStartDeadline = DateTime.Now.AddSeconds(FateExperimentalStartTimeoutSeconds);
                else if (DateTime.Now >= _fateContext.FillerStartDeadline)
                {
                    if (_fateContext.FinishFillerBeforeYield && Svc.Condition[ConditionFlag.InCombat])
                    {
                        _fateContext.FillerStartDeadline = DateTime.Now.AddSeconds(FateExperimentalStartTimeoutSeconds);
                        Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Filler {fate.FateId} still preparing after egress fallback; retaining combat ownership.");
                    }
                    else
                    {
                        FinishFateAutomation(
                            FateAutomationState.Failed,
                            FateExecutionResultKind.UnsupportedMechanic,
                            $"Experimental filler {FateMetadata.GetName(fate.FateId)} ({fate.FateId}) remained in Preparing for {FateExperimentalStartTimeoutSeconds}s without ZBR getting it started; backing off so the grinder can try another FATE.");
                        return;
                    }
                }
            }

            DriveFateStart(fate, prerequisite);
            return;
        }

        if (fate.State != DalamudFateState.Running)
        {
            _fateContext.AutomationStatus = $"Waiting for {FateMetadata.GetName(fate.FateId)} to become active (state {fate.State}).";
            return;
        }

        if (fate.Position == Vector3.Zero || fate.Radius <= 0f)
        {
            _fateContext.AutomationStatus = $"{FateMetadata.GetName(fate.FateId)} is active but its live location data is still loading.";
            return;
        }

        _fateContext.WorkingFateObservedRunning = true;
        if (!prerequisite)
            _fateContext.TargetObservedRunning = true;

        var currentFateId = GetCurrentFateId();
        if (currentFateId != fate.FateId)
        {
            if (_fateContext.EntryArrivalGraceUntil != DateTime.MinValue)
            {
                if (DateTime.Now < _fateContext.EntryArrivalGraceUntil)
                {
                    _fateContext.AutomationStatus = $"Waiting for the game to register entry into {FateMetadata.GetName(fate.FateId)} ({fate.FateId}).";
                    return;
                }

                if (_fateContext.DismountQueued || Svc.Condition[ConditionFlag.Mounted])
                {
                    QueueFateDismount();
                    _fateContext.AutomationStatus = $"Landing before retrying entry into {FateMetadata.GetName(fate.FateId)} ({fate.FateId}).";
                    return;
                }

                _fateContext.EntryArrivalGraceUntil = DateTime.MinValue;
                _fateContext.EntryArrivalMisses++;
                if (_fateContext.EntryArrivalMisses >= 2)
                {
                    FailFateAutomation($"Reached the entry and center of {FateMetadata.GetName(fate.FateId)} ({fate.FateId}), but the game still reports current FateId={currentFateId}.");
                    return;
                }
            }

            var forceCenterEntry = _fateContext.EntryArrivalMisses > 0;
            var destination = ResolveFateEntryDestination(fate, forceCenterEntry, out var entryIntent, out var anchorDescription);
            _fateContext.AutomationStatus = forceCenterEntry
                ? $"Moving deeper into {FateMetadata.GetName(fate.FateId)} ({fate.FateId}) to establish participation."
                : entryIntent == FateNavigationIntent.FateEntryAnchor
                    ? $"Approaching {anchorDescription} for a natural entry into {FateMetadata.GetName(fate.FateId)} ({fate.FateId})."
                    : $"Traveling into {FateMetadata.GetName(fate.FateId)} ({fate.FateId}) while waiting for live objectives to load.";
            var entryReason = entryIntent == FateNavigationIntent.FateEntryAnchor
                ? $"{anchorDescription} in live FATE {fate.FateId}"
                : $"live FATE {fate.FateId}";
            EnsureFateNavigation(destination, true, entryReason, entryIntent);
            return;
        }

        _fateContext.EntryArrivalMisses = 0;
        _fateContext.EntryArrivalGraceUntil = DateTime.MinValue;
        if (!prerequisite)
            _fateContext.TargetParticipated = true;

        if (TryYieldCompletedFiller(fate))
            return;

        if (Svc.Condition[ConditionFlag.Mounted] || Svc.Condition[ConditionFlag.InFlight])
        {
            if (TryContinueRegisteredFateEntry(fate))
                return;

            _fateContext.EntryAnchorId = 0;
            _fateContext.EntryAnchorDestination = null;
            QueueFateDismount();
            _fateContext.AutomationStatus = $"Landing inside {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        _fateContext.EntryAnchorId = 0;
        _fateContext.EntryAnchorDestination = null;

        if (_fateObstacleMaps.NeedsPreparation(fate.FateId) && _fateContext.NavigationActive)
            StopFateOwnedNavigation();

        if (!_fateObstacleMaps.EnsureReady(
                fate.FateId,
                FateMetadata.GetName(fate.FateId),
                Service.ClientState.TerritoryType,
                fate.Position,
                fate.Radius,
                Player.Object?.Position,
                "grounded player position after FATE entry"))
        {
            _fateContext.AutomationStatus = _fateObstacleMaps.Status;
            return;
        }

        TryLevelSyncToExactFate(fate.FateId);
        _fateContext.AutomationState = FateAutomationState.Fighting;
        DriveFateCombat(fate, prerequisite);
    }
    private bool TryYieldCompletedFiller(IFate fate)
    {
        if (!_fateContext.Request.IsFiller
            || fate.State != DalamudFateState.Running
            || fate.Progress < 100
            || IsCollectFate(fate.FateId) && _fateContext.CollectTurnInActive)
            return false;

        StopFateOwnedNavigation();
        ReleaseFateTacticalMovement();
        if (_experimentalGrinderTargetFreelyActive || _fateRotationSolver.IsAutoDutyOwned)
            ReleaseExperimentalGrinderCombatSession("the General Grinder FATE reached 100% and proven residual cleanup is taking over", true);
        FateTargeting.TrackVisibleFateEnemyIds(fate.FateId, _fateContext.KnownCombatEnemyIds);
        if (_fateContext.CombatTargetId != 0)
            _fateContext.KnownCombatEnemyIds.Add(_fateContext.CombatTargetId);
        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        _fateContext.InteractionTargetId = 0;
        _fateContext.ResidualAggroOrigin ??= Player.Object?.Position;
        _fateContext.ResidualLoggedTargetId = 0;
        ResetResidualAggroDiagnostics();
        _fateContext.PostCombatOutcome = FatePostCombatOutcome.TargetComplete;
        _fateContext.AutomationState = FateAutomationState.ClearingAggro;
        _fateContext.AutomationStatus = Svc.Condition[ConditionFlag.InCombat]
            ? $"{FateMetadata.GetName(fate.FateId)} reached 100%; clearing residual aggro before yielding to the grinder."
            : $"{FateMetadata.GetName(fate.FateId)} reached 100%; yielding to the grinder.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Filler {fate.FateId} reached 100%; beginning residual cleanup.");
        return true;
    }
    private void ObserveFateState(IFate fate, bool prerequisite)
    {
        var observingWorkingFate = _fateContext.WorkingFateId == fate.FateId
            && _fateContext.WorkingFateIsPrerequisite == prerequisite;
        if (observingWorkingFate)
        {
            _fateContext.WorkingFateMissingSince = DateTime.MinValue;
            if (fate.State == DalamudFateState.Preparing)
            {
                _fateContext.WorkingFateObservedPreparing = true;
                return;
            }
        }
        else if (fate.State == DalamudFateState.Preparing)
        {
            return;
        }

        if (fate.State == DalamudFateState.Running)
        {
            FateTargeting.TrackVisibleFateEnemyIds(fate.FateId, _fateContext.KnownCombatEnemyIds);
            _fateContext.WorkingFateObservedRunning = true;
            if (fate.Progress >= 100 && !_fateContext.WorkingFateCompletionObserved)
                _fateContext.WorkingFateCompletionObserved = true;
            if (!prerequisite)
                _fateContext.TargetObservedRunning = true;
            return;
        }

        if (_fateContext.WorkingFateObservedRunning && fate.State is DalamudFateState.Ending or DalamudFateState.Ended)
        {
            FateTargeting.TrackVisibleFateEnemyIds(fate.FateId, _fateContext.KnownCombatEnemyIds);
            _fateContext.WorkingFateCompletionObserved = true;
        }
    }
    private void SetWorkingFate(ushort fateId, bool prerequisite)
    {
        if (_fateContext.WorkingFateId == fateId && _fateContext.WorkingFateIsPrerequisite == prerequisite)
            return;

        _fateObstacleMaps.BeginWorkingFate(fateId);
        _fateContext.WorkingFateId = fateId;
        _fateContext.WorkingFateIsPrerequisite = prerequisite;
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Working {FateMetadata.GetName(fateId)} ({fateId}), prerequisite={prerequisite}.");
        _fateContext.WorkingFateObservedPreparing = false;
        _fateContext.WorkingFateObservedRunning = false;
        _fateContext.WorkingFateCompletionObserved = false;
        _fateContext.WorkingFateMissingSince = DateTime.MinValue;
        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        _fateContext.InteractionTargetId = 0;
        _fateContext.EscortAnchorId = 0;
        _fateContext.EscortFollowRestartAt = DateTime.MinValue;
        _fateContext.DefendAnchorId = 0;
        _fateContext.EventObjectSafeAt = DateTime.MinValue;
        _fateContext.CollectTurnInActive = false;
        _fateContext.CollectTurnInSafeAt = DateTime.MinValue;
        _fateContext.CollectTurnInStartItemCount = 0;
        _fateContext.CollectRequestFillSlot = 0;
        _fateContext.CollectRequestSubmitAfterFrame = 0;
        _fateContext.KnownCombatEnemyIds.Clear();
        _fateContext.ResidualAggroOrigin = null;
        _fateContext.ResidualLoggedTargetId = 0;
        _fateContext.EntryArrivalMisses = 0;
        _fateContext.EntryArrivalGraceUntil = DateTime.MinValue;
        _fateContext.EntryAnchorId = 0;
        _fateContext.EntryAnchorDestination = null;
        _fateContext.FillerStartDeadline = DateTime.MinValue;
    }
    private bool TryHandlePreparingBookFateGone(ushort fateId, bool prerequisite)
    {
        if (!_fateContext.HasBookTarget
            || fateId == 0
            || _fateContext.WorkingFateId != fateId
            || _fateContext.WorkingFateIsPrerequisite != prerequisite
            || !_fateContext.WorkingFateObservedPreparing
            || _fateContext.WorkingFateObservedRunning)
            return false;

        if (_fateContext.WorkingFateMissingSince == DateTime.MinValue)
        {
            _fateContext.WorkingFateMissingSince = DateTime.Now;
            _fateContext.AutomationStatus = $"{FateMetadata.GetName(fateId)} disappeared while still Preparing; waiting briefly for the local FATE table to settle.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Preparing FATE {fateId} disappeared; waiting {FatePreparingDisappearGraceMs}ms.");
            return true;
        }

        if ((DateTime.Now - _fateContext.WorkingFateMissingSince).TotalMilliseconds < FatePreparingDisappearGraceMs)
            return true;

        StopFateOwnedNavigation();
        var role = prerequisite ? "prerequisite" : "required";
        RetryFateLater($"Book {role} FATE {FateMetadata.GetName(fateId)} ({fateId}) disappeared while ZBR was trying to start it and never reached Running; continuing the book and retrying this FATE on a later sweep.");
        return true;
    }

    private void HandleObservedFateGone(bool prerequisite)
    {
        StopFateOwnedNavigation();
        FateTargeting.TrackVisibleFateEnemyIds(_fateContext.WorkingFateId, _fateContext.KnownCombatEnemyIds);
        if (_fateContext.CombatTargetId != 0)
            _fateContext.KnownCombatEnemyIds.Add(_fateContext.CombatTargetId);
        var stillTagged = FateTargeting.CountKnownEnemiesStillTagged(_fateContext.WorkingFateId, _fateContext.KnownCombatEnemyIds);
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Residual cleanup started for {_fateContext.WorkingFateId}, tracked={_fateContext.KnownCombatEnemyIds.Count}, tagged={stillTagged}.");
        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        _fateContext.ResidualAggroOrigin = Player.Object?.Position;
        _fateContext.ResidualLoggedTargetId = 0;
        ResetResidualAggroDiagnostics();

        var outcome = prerequisite
            ? (_fateContext.WorkingFateCompletionObserved ? FatePostCombatOutcome.PrerequisiteComplete : FatePostCombatOutcome.PrerequisiteRetry)
            : (!_fateContext.HasBookTarget && !_fateContext.WorkingFateCompletionObserved ? FatePostCombatOutcome.TargetFailed : FatePostCombatOutcome.TargetComplete);

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            _fateContext.PostCombatOutcome = outcome;
            _fateContext.AutomationState = FateAutomationState.ClearingAggro;
            _fateContext.AutomationStatus = $"{FateMetadata.GetName(_fateContext.WorkingFateId)} ended; clearing residual aggro before continuing.";
            return;
        }

        ApplyFatePostCombatOutcome(outcome);
    }
    private static bool TryGetFate(ushort fateId, out IFate fate)
    {
        foreach (var candidate in Svc.Fates)
        {
            if (candidate.FateId == fateId)
            {
                fate = candidate;
                return true;
            }
        }

        fate = default!;
        return false;
    }
}
