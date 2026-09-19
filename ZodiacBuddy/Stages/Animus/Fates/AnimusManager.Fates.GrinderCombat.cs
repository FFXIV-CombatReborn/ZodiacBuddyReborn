using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Combat;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const float GrinderBossMaxHpRatio = 2.5f;
    private const double GrinderRsrTargetReacquireGraceMs = 750d;
    private const double GrinderRsrStartupSettleMs = 600d;
    private const double GrinderMacroTargetCommitMs = 500d;
    private const byte GrinderMultiPullStopProgressPercent = 90;
    private const double GrinderMultiPullCandidateTimeoutMs = 7000d;
    private const double GrinderMultiPullProbeSettleMs = 1200d;
    private const double GrinderMultiPullRetrySuppressMs = 3000d;
    private const double GrinderMultiPullPressureLossDebounceMs = 350d;
    private const double GrinderMultiPullRetentionRecoveryMs = 5000d;
    private const float GrinderForlornPriorityRadius = 25f;
    private const uint ForlornNameId = 6737;
    private const uint ForlornMaidenNameId = 6738;

    private Vector3? _experimentalGrinderPackAnchor;
    private readonly HashSet<ulong> _experimentalGrinderPullProtectedThreatIds = new();
    private DateTime _experimentalGrinderPullPressureLossSince = DateTime.MinValue;
    private ulong _experimentalGrinderPullPressureLostId;
    private DateTime _experimentalGrinderRetentionHoldUntil = DateTime.MinValue;
    private int _experimentalGrinderAdaptivePackCeiling;

    private bool EnsureExperimentalGrinderRsrStartupSettled(
        IFate fate,
        IReadOnlyList<IBattleNpc> enemies,
        ExperimentalGrinderCombatPolicy combatPolicy,
        RotationSolverTargetingType targetingType,
        DateTime now)
    {
        if (_experimentalGrinderRsrStartupHandoffCompleted)
            return true;

        if (GetCurrentFateId() != fate.FateId)
        {
            _experimentalGrinderRsrStartupConfirmedSince = DateTime.MinValue;
            _fateContext.AutomationStatus =
                $"Experimental grinder: waiting for FateId={fate.FateId} to remain registered before RSR AutoDuty is armed.";
            return false;
        }

        if (_experimentalGrinderRsrStartupConfirmedSince == DateTime.MinValue)
            _experimentalGrinderRsrStartupConfirmedSince = now;

        var elapsedMs = (now - _experimentalGrinderRsrStartupConfirmedSince).TotalMilliseconds;
        if (elapsedMs < GrinderRsrStartupSettleMs)
        {
            _fateContext.AutomationStatus =
                $"Experimental grinder: FATE entry confirmed; waiting {GrinderRsrStartupSettleMs - elapsedMs:F0}ms before enabling RSR AutoDuty to avoid an entry-frame outside-FATE target.";
            return false;
        }

        var handoffTarget = GetCurrentExperimentalGrinderTarget(fate.FateId)
            ?? SelectExperimentalGrinderSeedTarget(enemies, combatPolicy);
        var existingTarget = Svc.Targets.Target as IBattleNpc;
        if (handoffTarget != null
            && (existingTarget == null || !FateTargeting.IsFateEnemy(existingTarget, fate.FateId)))
        {
            Svc.Targets.Target = handoffTarget;
            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-COMBAT] Seeded startup target '{handoffTarget.Name.TextValue}' ({handoffTarget.GameObjectId}), targeting={targetingType}.");
        }

        _experimentalGrinderRsrStartupHandoffCompleted = true;
        return true;
    }

    private bool TryDriveExperimentalGrinderCombat(IFate fate)
    {
        if (!IsExperimentalGrinderCombatEligibleFate(fate))
        {
            if (_experimentalGrinderTargetFreelyActive || _fateRotationSolver.IsAutoDutyOwned)
            {
                var reason = _fateContext.Request.Purpose != FateExecutionPurpose.GeneralGrinder
                    || !Service.Configuration.FateGrinder.ExperimentalCombat
                        ? "experimental General Grinder combat is not active for this execution path"
                        : "this FATE has known special/profile combat handling and remains on the proven executor";
                ReleaseExperimentalGrinderCombatSession(reason, true);
            }
            return false;
        }

        if (!EnsureExperimentalGrinderPresetReady(out var presetDetail))
        {
            ReleaseExperimentalGrinderCombatSession("the required explicit RSR grinder preset is not active", true);
            LogExperimentalGrinderFallbackOnce(
                "rsr-preset",
                $"FateId={fate.FateId} required RSR grinder preset is not active: {presetDetail}");
            return false;
        }

        var enemies = GetLoadedGrinderFateEnemies(fate.FateId);
        if (enemies.Count == 0)
            return DriveExperimentalGrinderNoObjectiveTargets(fate);

        var combatPolicy = ClassifyExperimentalGrinderCombat(enemies);
        var targetingType = combatPolicy == ExperimentalGrinderCombatPolicy.Boss
            ? RotationSolverTargetingType.HighMaxHP
            : RotationSolverTargetingType.Nearest;

        var now = DateTime.Now;
        if (!EnsureExperimentalGrinderRsrStartupSettled(fate, enemies, combatPolicy, targetingType, now))
            return true;

        if (!EnsureExperimentalGrinderRotationSolver(targetingType))
        {
            ReleaseExperimentalGrinderCombatSession("RSR AutoDuty mode/TargetFreely could not be acquired", true);
            LogExperimentalGrinderFallbackOnce(
                "rsr-auto-mode",
                $"FateId={fate.FateId} could not acquire the required RSR AutoDuty mode/TargetFreely controls.");
            return false;
        }

        foreach (var enemy in enemies)
            _fateContext.KnownCombatEnemyIds.Add(enemy.GameObjectId);

        now = DateTime.Now;
        var target = GetCurrentExperimentalGrinderTarget(fate.FateId);
        if (target == null)
        {
            if (Svc.Targets.Target is IBattleNpc invalidTarget
                && FateTargeting.IsAttackableEnemy(invalidTarget)
                && !FateTargeting.IsFateEnemy(invalidTarget, fate.FateId))
            {
                target = SelectExperimentalGrinderSeedTarget(enemies, combatPolicy);
                if (target != null)
                {
                    Svc.Targets.Target = target;
                    _experimentalGrinderRsrTargetMissingSince = DateTime.MinValue;
                    ClearExperimentalGrinderMacroTargetCandidate();
                    Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-COMBAT] Replaced outside-FATE target '{invalidTarget.Name.TextValue}' with '{target.Name.TextValue}' ({target.GameObjectId}).");
                }
            }
            else
            {
                if (_experimentalGrinderRsrTargetMissingSince == DateTime.MinValue)
                {
                    _experimentalGrinderRsrTargetMissingSince = now;
                    _fateContext.CombatTargetId = 0;
                    _fateCombatEngagement.Reset();
                    _experimentalGrinderMacroAnchorId = 0;
                    ClearExperimentalGrinderMacroTargetCandidate();
                    if (_fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.CombatTarget)
                        StopFateOwnedNavigation();

                }

                var missingMs = (now - _experimentalGrinderRsrTargetMissingSince).TotalMilliseconds;
                if (missingMs < GrinderRsrTargetReacquireGraceMs)
                {
                    _fateContext.AutomationStatus =
                        $"Experimental grinder: waiting {GrinderRsrTargetReacquireGraceMs - missingMs:F0}ms for RSR to choose the next current-FATE target; ZBR targeting is idle.";
                    return true;
                }

                target = SelectExperimentalGrinderSeedTarget(enemies, combatPolicy);
                if (target != null)
                {
                    Svc.Targets.Target = target;
                    _experimentalGrinderRsrTargetMissingSince = DateTime.MinValue;
                    Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-COMBAT] Seeded fallback target '{target.Name.TextValue}' ({target.GameObjectId}) after {missingMs:F0}ms.");
                }
            }
        }
        else if (_experimentalGrinderRsrTargetMissingSince != DateTime.MinValue)
        {
            _experimentalGrinderRsrTargetMissingSince = DateTime.MinValue;
        }

        if (target == null)
        {
            return DriveExperimentalGrinderNoObjectiveTargets(fate);
        }

        var player = Player.Object!;
        var actualThreatIds = FateTargeting.GetCurrentFateThreatIds(fate.FateId);
        RefreshExperimentalGrinderPackAnchor(fate, combatPolicy, player, actualThreatIds);

        if (_experimentalGrinderPullCandidateId == 0
            && Service.Configuration.FateGrinder.MultiPullEnabled
            && combatPolicy == ExperimentalGrinderCombatPolicy.GenericPack
            && actualThreatIds.Count > 0
            && !actualThreatIds.Contains(target.GameObjectId)
            && !IsForlorn(target))
        {
            if (TryStartExperimentalGrinderMultiPull(fate, enemies, target, combatPolicy, player, false, out var retentionPullStatus))
            {
                _fateContext.AutomationStatus = retentionPullStatus;
                return true;
            }

            if (_experimentalGrinderPullCandidateId != 0)
            {
                StopFateOwnedNavigation();
                _experimentalGrinderMacroAnchorId = 0;
                ClearExperimentalGrinderMacroTargetCandidate();
                _fateContext.AutomationStatus =
                    $"Experimental grinder: committed multi-pull candidate is being held ({_experimentalGrinderPullHoldReason}); RSR stays active but ordinary BMR/vnav target-follow movement is deferred.";
                return true;
            }

            var rejectedTarget = target;
            var retentionReason = BuildExperimentalGrinderRsrRetentionReason(
                rejectedTarget,
                enemies,
                actualThreatIds,
                Service.Configuration.FateGrinder.EffectiveMultiPullRadius);
            var retainedTarget = RestoreExperimentalGrinderRetainedPackTarget(
                fate.FateId,
                actualThreatIds,
                rejectedTarget.GameObjectId,
                retentionReason);

            StopFateOwnedNavigation();
            _experimentalGrinderMacroAnchorId = 0;
            ClearExperimentalGrinderMacroTargetCandidate();

            if (retainedTarget != null)
            {
                target = retainedTarget;
                Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Kept retained pressure instead of chasing '{rejectedTarget.Name.TextValue}': {retentionReason}.");
            }
            else
            {
                _fateContext.AutomationStatus =
                    $"Experimental grinder: refusing to chase unengaged {rejectedTarget.Name.TextValue} because a retained pack is active; waiting for retained pressure to resolve.";
                return true;
            }
        }

        _fateContext.NoCombatTargetSince = DateTime.MinValue;
        _fateContext.CombatTargetId = target.GameObjectId;
        _fateContext.KnownCombatEnemyIds.Add(target.GameObjectId);
        _fateCombatEngagement.Reset();

        var horizontalDistance = MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(player.Position, target.Position));
        var macroHandoffDistance = GetFateCombatMacroHandoffDistance(target);
        if (_experimentalGrinderPullCandidateId != 0)
        {
            if (TryStartExperimentalGrinderMultiPull(fate, enemies, target, combatPolicy, player, true, out var committedPullStatus))
            {
                _fateContext.AutomationStatus = committedPullStatus;
                return true;
            }

            if (_experimentalGrinderPullCandidateId != 0)
            {
                StopFateOwnedNavigation();
                _experimentalGrinderMacroAnchorId = 0;
                ClearExperimentalGrinderMacroTargetCandidate();
                _fateContext.AutomationStatus =
                    $"Experimental grinder: committed multi-pull candidate is being held ({_experimentalGrinderPullHoldReason}); RSR stays active but ordinary BMR/vnav target-follow movement is deferred.";
                return true;
            }
        }

        var macroNavigationActive = _fateContext.NavigationActive
            && _fateContext.NavigationIntent == FateNavigationIntent.CombatTarget;
        if (macroNavigationActive
            && _experimentalGrinderMacroAnchorId != 0
            && _experimentalGrinderMacroAnchorId != target.GameObjectId)
        {
            if (IsLoadedExperimentalGrinderFateEnemy(_experimentalGrinderMacroAnchorId, fate.FateId))
            {
                if (!IsExperimentalGrinderMacroTargetStable(target.GameObjectId, now, out var remainingMs))
                {
                    _fateContext.AutomationStatus =
                        $"Experimental grinder: RSR retargeted to {target.Name.TextValue}; holding the existing vnav anchor for {remainingMs:F0}ms before any macro repath.";
                    return true;
                }

                _experimentalGrinderMacroAnchorId = 0;
            }
            else
            {
                StopFateOwnedNavigation();
                _experimentalGrinderMacroAnchorId = 0;
                ClearExperimentalGrinderMacroTargetCandidate();
            }
        }

        if (horizontalDistance > macroHandoffDistance)
        {
            if (!(macroNavigationActive && _experimentalGrinderMacroAnchorId == target.GameObjectId)
                && !IsExperimentalGrinderMacroTargetStable(target.GameObjectId, now, out var remainingMs))
            {
                var waitTelemetryAvailable = BossModIPC.TryGetNavigationState(out _);
                if (waitTelemetryAvailable)
                    _ = _fateTacticalMovement.Acquire(player.ClassJob.RowId, false);

                _fateContext.AutomationStatus =
                    $"Experimental grinder: RSR chose {target.Name.TextValue} at {horizontalDistance:F1}y; waiting {remainingMs:F0}ms before committing a vnav approach.";
                return true;
            }

            ClearExperimentalGrinderMacroTargetCandidate();
            ReleaseFateTacticalMovement();
            EnsureFateNavigation(target.Position, false, $"experimental grinder combat target {target.GameObjectId}", FateNavigationIntent.CombatTarget);
            _experimentalGrinderMacroAnchorId = target.GameObjectId;
            _fateContext.AutomationStatus = $"Experimental grinder: vnav is closing on {target.Name.TextValue} ({horizontalDistance:F1}y) while RSR AutoDuty mode stays active and may attack/retarget during the approach.";
            return true;
        }

        StopFateOwnedNavigation();
        _experimentalGrinderMacroAnchorId = 0;

        if (TryStartExperimentalGrinderMultiPull(fate, enemies, target, combatPolicy, player, true, out var multiPullStatus))
        {
            _fateContext.AutomationStatus = multiPullStatus;
            return true;
        }

        var bossModTelemetryAvailable = BossModIPC.TryGetNavigationState(out _);
        if (bossModTelemetryAvailable && _fateTacticalMovement.Acquire(player.ClassJob.RowId, false))
        {
            ClearExperimentalGrinderMacroTargetCandidate();
            _fateTerrainRecovery.Tick(_fateTacticalMovement, target.GameObjectId, player.Position);
            if (_fateTerrainRecovery.IsRecovering)
            {
                _fateContext.AutomationStatus = $"Experimental grinder: {_fateTerrainRecovery.Status} RSR remains active during the scoped recovery.";
                return true;
            }

            var moving = BossModIPC.TryGetNavigationState(out var navigationState) && navigationState.MovementActive;
            _fateContext.AutomationStatus = $"Experimental grinder: RSR AutoDuty mode/{targetingType} is always-on while BossMod follows the live hard target {target.Name.TextValue} ({horizontalDistance:F1}y, BMR moving={moving}).";
            return true;
        }

        ReleaseFateTacticalMovement();
        var distance = Vector3.Distance(player.Position, target.Position);
        if (distance > FateCombatEngageDistance)
        {
            if (!IsExperimentalGrinderMacroTargetStable(target.GameObjectId, now, out var remainingMs))
            {
                _fateContext.AutomationStatus =
                    $"Experimental grinder: BossMod is unavailable; waiting {remainingMs:F0}ms for RSR target stability before vnav fallback.";
                return true;
            }

            ClearExperimentalGrinderMacroTargetCandidate();
            EnsureFateNavigation(target.Position, false, $"experimental grinder vnav fallback target {target.GameObjectId}", FateNavigationIntent.CombatTarget);
            _experimentalGrinderMacroAnchorId = target.GameObjectId;
            _fateContext.AutomationStatus = $"Experimental grinder: BossMod tactical movement is unavailable; vnav is approaching {target.Name.TextValue} while RSR remains active ({distance:F1}y).";
        }
        else
        {
            ClearExperimentalGrinderMacroTargetCandidate();
            StopFateOwnedNavigation();
            _experimentalGrinderMacroAnchorId = 0;
            _fateContext.AutomationStatus = $"Experimental grinder: BossMod tactical movement is unavailable; fighting {target.Name.TextValue} with always-on RSR and no forced movement.";
        }

        return true;
    }

    private bool TryStartExperimentalGrinderMultiPull(
        IFate fate,
        IReadOnlyList<IBattleNpc> enemies,
        IBattleNpc currentTarget,
        ExperimentalGrinderCombatPolicy combatPolicy,
        IPlayerCharacter player,
        bool countCurrentTargetAsEngaged,
        out string status)
    {
        status = string.Empty;
        var config = Service.Configuration.FateGrinder;
        if (!config.MultiPullEnabled || combatPolicy != ExperimentalGrinderCombatPolicy.GenericPack)
        {
            ClearExperimentalGrinderPullCandidate();
            ClearExperimentalGrinderPullHold();
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        var jobAbbreviation = FateGrinderMultiPullProfiles.GetAbbreviation(classJobId);
        var configuredPackSize = FateGrinderMultiPullProfiles.GetPackSize(config, classJobId);
        if (configuredPackSize <= 1)
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold($"{jobAbbreviation} profile is set to a single target");
            return false;
        }

        var actualThreatIds = FateTargeting.GetCurrentFateThreatIds(fate.FateId);
        if (actualThreatIds.Count == 0 && _experimentalGrinderPullCandidateId == 0)
        {
            _experimentalGrinderPackAnchor = null;
            SetExperimentalGrinderPullHold("waiting for the first current-FATE enemy to establish real pressure before adding another pull");
            return false;
        }

        var desiredPackSize = _experimentalGrinderAdaptivePackCeiling > 0
            ? Math.Min(configuredPackSize, _experimentalGrinderAdaptivePackCeiling)
            : configuredPackSize;
        var pendingCandidate = FindLoadedExperimentalGrinderFateEnemy(_experimentalGrinderPullCandidateId, fate.FateId);
        var candidateActuallyEngaged = pendingCandidate != null && actualThreatIds.Contains(pendingCandidate.GameObjectId);

        var engagedIds = new HashSet<ulong>(actualThreatIds);
        if (countCurrentTargetAsEngaged
            && (_experimentalGrinderPullCandidateId == 0 || currentTarget.GameObjectId != _experimentalGrinderPullCandidateId || candidateActuallyEngaged))
            engagedIds.Add(currentTarget.GameObjectId);
        var engagedCount = engagedIds.Count;
        var now = DateTime.Now;

        if (_experimentalGrinderPullCandidateId != 0
            && TryDetectExperimentalGrinderProtectedPressureLoss(fate.FateId, actualThreatIds, now, out var lostThreatId, out var lostThreatName))
        {
            var candidateId = _experimentalGrinderPullCandidateId;
            var candidateName = pendingCandidate?.Name.TextValue ?? candidateId.ToString();
            if (_fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.PullCandidate)
                StopFateOwnedNavigation();

            var retainedCount = Math.Max(2, actualThreatIds.Count);
            var previousCeiling = _experimentalGrinderAdaptivePackCeiling > 0
                ? _experimentalGrinderAdaptivePackCeiling
                : configuredPackSize;
            _experimentalGrinderAdaptivePackCeiling = Math.Min(previousCeiling, retainedCount);
            _experimentalGrinderRetentionHoldUntil = now.AddMilliseconds(GrinderMultiPullRetentionRecoveryMs);
            RestoreExperimentalGrinderRetainedPackTarget(
                fate.FateId,
                actualThreatIds,
                candidateId,
                "a deliberate pull caused previously engaged pressure to drop");
            SuppressExperimentalGrinderPullCandidate(candidateId, now);
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold(
                $"pack retention loss detected on {lostThreatName}; adaptive ceiling {_experimentalGrinderAdaptivePackCeiling}/{configuredPackSize}");
            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Aborted '{candidateName}' ({candidateId}); lost pressure on '{lostThreatName}' ({lostThreatId}), pack ceiling={_experimentalGrinderAdaptivePackCeiling}/{configuredPackSize}.");
            return false;
        }

        if (candidateActuallyEngaged)
        {
            var acquiredName = pendingCandidate!.Name.TextValue;
            var acquiredId = pendingCandidate.GameObjectId;
            if (_fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.PullCandidate)
                StopFateOwnedNavigation();
            ClearExperimentalGrinderPullCandidate();
            _experimentalGrinderNextPullAt = DateTime.Now.AddMilliseconds(600);
            ClearExperimentalGrinderPullHold();
            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Acquired '{acquiredName}' ({acquiredId}), pack={actualThreatIds.Count}/{desiredPackSize}.");
            return false;
        }

        if (engagedCount >= desiredPackSize)
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold($"pack full {engagedCount}/{desiredPackSize}{(desiredPackSize != configuredPackSize ? $" (configured {configuredPackSize}; retention-capped)" : string.Empty)}");
            return false;
        }

        if (player.MaxHp == 0)
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold("player HP is unavailable");
            return false;
        }

        var hpPercent = (int)MathF.Round(player.CurrentHp * 100f / player.MaxHp);
        if (hpPercent < config.EffectiveMultiPullHpFloorPercent)
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold($"HP {hpPercent}% is below {config.EffectiveMultiPullHpFloorPercent}%");
            return false;
        }

        if (fate.Progress >= GrinderMultiPullStopProgressPercent)
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold($"FATE progress is {fate.Progress}%");
            return false;
        }

        if (HasNearbyForlorn(enemies, player.Position))
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold("Forlorn priority target is nearby");
            return false;
        }

        _ = FateTargeting.GetBestDirectThreatTarget(
            fate.FateId,
            0,
            fate.Position,
            fate.Radius,
            true,
            out var externalThreats,
            out _,
            out _,
            out _);
        if (externalThreats > 0)
        {
            ClearExperimentalGrinderPullCandidate();
            SetExperimentalGrinderPullHold($"external threat pressure={externalThreats}");
            return false;
        }

        var pullRadius = config.EffectiveMultiPullRadius;
        var pullRadiusSquared = pullRadius * pullRadius;
        if (now < _experimentalGrinderRetentionHoldUntil)
        {
            var remainingMs = (_experimentalGrinderRetentionHoldUntil - now).TotalMilliseconds;
            SetExperimentalGrinderPullHold($"consolidating retained pack after aggro loss ({remainingMs:F0}ms)");
            return false;
        }

        var maxPackSpread = pullRadius + 4f;
        var maxPackSpreadSquared = maxPackSpread * maxPackSpread;
        foreach (var enemy in enemies)
        {
            if (engagedIds.Contains(enemy.GameObjectId)
                && NavigationGeometry.HorizontalDistanceSquared(player.Position, enemy.Position) > maxPackSpreadSquared)
            {
                ClearExperimentalGrinderPullCandidate();
                SetExperimentalGrinderPullHold($"current pack is already spread beyond {maxPackSpread:F0}y");
                return false;
            }
        }

        if (_experimentalGrinderPullCandidateId != 0)
        {
            if (pendingCandidate == null)
            {
                var lostId = _experimentalGrinderPullCandidateId;
                ClearExperimentalGrinderPullCandidate();
                _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                SetExperimentalGrinderPullHold($"pull candidate {lostId} disappeared");
                return false;
            }

            if (!IsExperimentalGrinderCandidateInsideRetentionEnvelope(
                    pendingCandidate,
                    enemies,
                    engagedIds,
                    pullRadius,
                    maxPackSpread,
                    out var retentionReason))
            {
                var abandonedId = pendingCandidate.GameObjectId;
                var abandonedName = pendingCandidate.Name.TextValue;
                RestoreExperimentalGrinderRetainedPackTarget(
                    fate.FateId,
                    actualThreatIds,
                    abandonedId,
                    retentionReason);
                SuppressExperimentalGrinderPullCandidate(abandonedId, now);
                ClearExperimentalGrinderPullCandidate();
                _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                SetExperimentalGrinderPullHold(retentionReason);
                Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Abandoned '{abandonedName}' ({abandonedId}): {retentionReason}.");
                return false;
            }

            var candidateDistanceSquared = NavigationGeometry.HorizontalDistanceSquared(player.Position, pendingCandidate.Position);
            if (candidateDistanceSquared > pullRadiusSquared)
            {
                var abandonedId = pendingCandidate.GameObjectId;
                var abandonedName = pendingCandidate.Name.TextValue;
                SuppressExperimentalGrinderPullCandidate(abandonedId, now);
                ClearExperimentalGrinderPullCandidate();
                SetExperimentalGrinderPullHold($"committed candidate {abandonedName} moved outside {pullRadius:F0}y");
                return false;
            }

            if (_experimentalGrinderPullCandidateSince != DateTime.MinValue
                && (now - _experimentalGrinderPullCandidateSince).TotalMilliseconds >= GrinderMultiPullCandidateTimeoutMs)
            {
                var timedOutId = pendingCandidate.GameObjectId;
                var timedOutName = pendingCandidate.Name.TextValue;
                SuppressExperimentalGrinderPullCandidate(timedOutId, now);
                ClearExperimentalGrinderPullCandidate();
                _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                SetExperimentalGrinderPullHold($"candidate {timedOutName} did not join pressure within {GrinderMultiPullCandidateTimeoutMs / 1000d:F0}s");
                Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Timed out '{timedOutName}' ({timedOutId}); suppressed for {GrinderMultiPullRetrySuppressMs / 1000d:F0}s.");
                return false;
            }

            if (_fateContext.NavigationActive && _fateContext.NavigationIntent == FateNavigationIntent.PullCandidate)
            {
                status =
                    $"Experimental grinder: {jobAbbreviation} multi-pull {engagedCount}/{desiredPackSize}; committed pull candidate {pendingCandidate.Name.TextValue} while RSR freely retargets/attacks.";
                return true;
            }

            if (_experimentalGrinderPullProbeUntil != DateTime.MinValue)
            {
                if (now < _experimentalGrinderPullProbeUntil)
                {
                    var remainingMs = (_experimentalGrinderPullProbeUntil - now).TotalMilliseconds;
                    ClearExperimentalGrinderPullHold();
                    status =
                        $"Experimental grinder: close-pull probe on committed {pendingCandidate.Name.TextValue}; waiting {remainingMs:F0}ms for real pressure while RSR remains active and movement stays neutral.";
                    return true;
                }

                var failedId = pendingCandidate.GameObjectId;
                var failedName = pendingCandidate.Name.TextValue;
                SuppressExperimentalGrinderPullCandidate(failedId, now);
                ClearExperimentalGrinderPullCandidate();
                _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
                SetExperimentalGrinderPullHold($"close pass on {failedName} did not establish pressure");
                Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Probe missed '{failedName}' ({failedId}); suppressed for {GrinderMultiPullRetrySuppressMs / 1000d:F0}s.");
                return false;
            }

            if (now < _experimentalGrinderNextPullAt)
            {
                SetExperimentalGrinderPullHold($"holding committed candidate {pendingCandidate.Name.TextValue} before retrying its local path");
                return false;
            }

            ClearExperimentalGrinderPullHold();
            return StartExperimentalGrinderPullNudge(
                pendingCandidate,
                currentTarget,
                player,
                jobAbbreviation,
                engagedCount,
                desiredPackSize,
                pullRadius,
                now,
                out status,
                continuingCandidate: true);
        }

        if (now < _experimentalGrinderNextPullAt)
        {
            SetExperimentalGrinderPullHold($"settling pack {engagedCount}/{desiredPackSize}");
            return false;
        }

        IBattleNpc? candidate = null;
        var bestDistanceSquared = float.MaxValue;
        string? nearestRetentionRejection = null;
        var nearestRejectedDistanceSquared = float.MaxValue;
        foreach (var enemy in enemies)
        {
            if (engagedIds.Contains(enemy.GameObjectId)
                || IsForlorn(enemy)
                || IsExperimentalGrinderPullCandidateSuppressed(enemy.GameObjectId, now))
                continue;

            var distanceSquared = NavigationGeometry.HorizontalDistanceSquared(player.Position, enemy.Position);
            if (distanceSquared > pullRadiusSquared)
                continue;

            if (!IsExperimentalGrinderCandidateInsideRetentionEnvelope(
                    enemy,
                    enemies,
                    engagedIds,
                    pullRadius,
                    maxPackSpread,
                    out var retentionReason))
            {
                if (distanceSquared < nearestRejectedDistanceSquared)
                {
                    nearestRejectedDistanceSquared = distanceSquared;
                    nearestRetentionRejection = retentionReason;
                }
                continue;
            }

            if (distanceSquared >= bestDistanceSquared)
                continue;

            candidate = enemy;
            bestDistanceSquared = distanceSquared;
        }

        if (candidate == null)
        {
            SetExperimentalGrinderPullHold(nearestRetentionRejection ?? $"no unengaged FATE enemy within {pullRadius:F0}y");
            return false;
        }

        _experimentalGrinderPullProtectedThreatIds.Clear();
        foreach (var threatId in actualThreatIds)
            _experimentalGrinderPullProtectedThreatIds.Add(threatId);
        _experimentalGrinderPullPressureLossSince = DateTime.MinValue;
        _experimentalGrinderPullPressureLostId = 0;
        _experimentalGrinderPullCandidateId = candidate.GameObjectId;
        _experimentalGrinderPullCandidateSince = now;
        _experimentalGrinderNextPullAt = DateTime.MinValue;
        ClearExperimentalGrinderPullHold();
        return StartExperimentalGrinderPullNudge(
            candidate,
            currentTarget,
            player,
            jobAbbreviation,
            engagedCount,
            desiredPackSize,
            pullRadius,
            now,
            out status,
            continuingCandidate: false);
    }

    private bool StartExperimentalGrinderPullNudge(
        IBattleNpc candidate,
        IBattleNpc currentTarget,
        IPlayerCharacter player,
        string jobAbbreviation,
        int engagedCount,
        int desiredPackSize,
        float pullRadius,
        DateTime now,
        out string status,
        bool continuingCandidate)
    {
        status = string.Empty;
        var candidateDistance = MathF.Sqrt(NavigationGeometry.HorizontalDistanceSquared(player.Position, candidate.Position));
        var arrivalDistance = GetExperimentalGrinderPullArrivalDistance(candidate);
        ClearExperimentalGrinderMacroTargetCandidate();
        EnsureFateNavigation(
            candidate.Position,
            false,
            $"local multi-pull candidate {candidate.GameObjectId}",
            FateNavigationIntent.PullCandidate,
            arrivalDistance);
        if (!_fateContext.NavigationActive || _fateContext.NavigationIntent != FateNavigationIntent.PullCandidate)
        {
            _experimentalGrinderNextPullAt = now.AddMilliseconds(600);
            SetExperimentalGrinderPullHold("local pull navigation was unavailable");
            return false;
        }

        if (!continuingCandidate)
            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-PULL] Pulling '{candidate.Name.TextValue}' ({candidate.GameObjectId}), pack={engagedCount}/{desiredPackSize}, distance={candidateDistance:F1}y.");
        status =
            $"Experimental grinder: {jobAbbreviation} multi-pull {engagedCount}/{desiredPackSize}; " +
            $"committed acquisition of {candidate.Name.TextValue} ({candidateDistance:F1}y) while RSR keeps target/action ownership.";
        return true;
    }

    private static float GetExperimentalGrinderPullArrivalDistance(IBattleNpc candidate)
    {
        return Math.Clamp(candidate.HitboxRadius + 0.75f, 2.25f, 4f);
    }

    private void ClearExperimentalGrinderPullCandidate()
    {
        _experimentalGrinderPullCandidateId = 0;
        _experimentalGrinderPullCandidateSince = DateTime.MinValue;
        _experimentalGrinderPullProbeUntil = DateTime.MinValue;
        _experimentalGrinderPullProtectedThreatIds.Clear();
        _experimentalGrinderPullPressureLossSince = DateTime.MinValue;
        _experimentalGrinderPullPressureLostId = 0;
    }

    private void ResetExperimentalGrinderRetentionForNewRun()
    {
        _experimentalGrinderPackAnchor = null;
        _experimentalGrinderPullProtectedThreatIds.Clear();
        _experimentalGrinderPullPressureLossSince = DateTime.MinValue;
        _experimentalGrinderPullPressureLostId = 0;
        _experimentalGrinderRetentionHoldUntil = DateTime.MinValue;
        _experimentalGrinderAdaptivePackCeiling = 0;
    }

    private bool TryDetectExperimentalGrinderProtectedPressureLoss(
        ushort fateId,
        HashSet<ulong> actualThreatIds,
        DateTime now,
        out ulong lostThreatId,
        out string lostThreatName)
    {
        lostThreatId = 0;
        lostThreatName = string.Empty;
        if (_experimentalGrinderPullProtectedThreatIds.Count == 0)
        {
            _experimentalGrinderPullPressureLossSince = DateTime.MinValue;
            _experimentalGrinderPullPressureLostId = 0;
            return false;
        }

        ulong missingId = 0;
        string missingName = string.Empty;
        foreach (var protectedId in _experimentalGrinderPullProtectedThreatIds.ToArray())
        {
            if (actualThreatIds.Contains(protectedId))
                continue;

            var stillAlive = FindLoadedExperimentalGrinderFateEnemy(protectedId, fateId);
            if (stillAlive == null)
            {
                _experimentalGrinderPullProtectedThreatIds.Remove(protectedId);
                continue;
            }

            missingId = protectedId;
            missingName = stillAlive.Name.TextValue;
            break;
        }

        if (missingId == 0)
        {
            _experimentalGrinderPullPressureLossSince = DateTime.MinValue;
            _experimentalGrinderPullPressureLostId = 0;
            return false;
        }

        if (_experimentalGrinderPullPressureLostId != missingId)
        {
            _experimentalGrinderPullPressureLostId = missingId;
            _experimentalGrinderPullPressureLossSince = now;
            return false;
        }

        if (_experimentalGrinderPullPressureLossSince == DateTime.MinValue)
            _experimentalGrinderPullPressureLossSince = now;

        if ((now - _experimentalGrinderPullPressureLossSince).TotalMilliseconds < GrinderMultiPullPressureLossDebounceMs)
            return false;

        lostThreatId = missingId;
        lostThreatName = missingName;
        return true;
    }

    private void RefreshExperimentalGrinderPackAnchor(
        IFate fate,
        ExperimentalGrinderCombatPolicy combatPolicy,
        IPlayerCharacter player,
        HashSet<ulong> actualThreatIds)
    {
        var config = Service.Configuration.FateGrinder;
        if (!config.MultiPullEnabled || combatPolicy != ExperimentalGrinderCombatPolicy.GenericPack)
        {
            _experimentalGrinderPackAnchor = null;
            return;
        }

        if (actualThreatIds.Count == 0)
        {
            if (_experimentalGrinderPullCandidateId == 0 && _experimentalGrinderPackAnchor != null)
            {
                _experimentalGrinderPackAnchor = null;
                _experimentalGrinderRetentionHoldUntil = DateTime.MinValue;
            }

            return;
        }

        if (_experimentalGrinderPackAnchor != null)
            return;

        _experimentalGrinderPackAnchor = player.Position;
    }

    private string BuildExperimentalGrinderRsrRetentionReason(
        IBattleNpc target,
        IReadOnlyList<IBattleNpc> enemies,
        HashSet<ulong> actualThreatIds,
        float pullRadius)
    {
        if (!IsExperimentalGrinderCandidateInsideRetentionEnvelope(
                target,
                enemies,
                actualThreatIds,
                pullRadius,
                pullRadius + 4f,
                out var envelopeReason))
            return envelopeReason;

        return "RSR selected a current-FATE mob that is not part of actual retained pressure; only the deliberate multi-pull planner may physically acquire additional mobs while a pack is active";
    }

    private bool IsExperimentalGrinderCandidateInsideRetentionEnvelope(
        IBattleNpc candidate,
        IReadOnlyList<IBattleNpc> enemies,
        HashSet<ulong> engagedIds,
        float pullRadius,
        float maxPackSpread,
        out string reason)
    {
        reason = string.Empty;
        if (_experimentalGrinderPackAnchor is Vector3 packAnchor
            && NavigationGeometry.HorizontalDistanceSquared(packAnchor, candidate.Position) > pullRadius * pullRadius)
        {
            reason = $"candidate {candidate.Name.TextValue} would leave the local pack anchor by more than {pullRadius:F0}y";
            return false;
        }

        var maxPackSpreadSquared = maxPackSpread * maxPackSpread;
        foreach (var enemy in enemies)
        {
            if (!engagedIds.Contains(enemy.GameObjectId) || enemy.GameObjectId == candidate.GameObjectId)
                continue;

            if (NavigationGeometry.HorizontalDistanceSquared(enemy.Position, candidate.Position) <= maxPackSpreadSquared)
                continue;

            reason = $"candidate {candidate.Name.TextValue} would stretch the retained pack beyond {maxPackSpread:F0}y from {enemy.Name.TextValue}";
            return false;
        }

        return true;
    }

    private IBattleNpc? RestoreExperimentalGrinderRetainedPackTarget(
        ushort fateId,
        HashSet<ulong> actualThreatIds,
        ulong rejectedCandidateId,
        string reason)
    {
        if (actualThreatIds.Count == 0)
            return null;

        if (Svc.Targets.Target is IBattleNpc existing && actualThreatIds.Contains(existing.GameObjectId))
            return existing;

        var player = Player.Object;
        IBattleNpc? retainedTarget = null;
        var bestDistanceSquared = float.MaxValue;
        foreach (var threatId in actualThreatIds)
        {
            var threat = FindLoadedExperimentalGrinderFateEnemy(threatId, fateId);
            if (threat == null)
                continue;

            var distanceSquared = player == null
                ? 0f
                : NavigationGeometry.HorizontalDistanceSquared(player.Position, threat.Position);
            if (retainedTarget != null && distanceSquared >= bestDistanceSquared)
                continue;

            retainedTarget = threat;
            bestDistanceSquared = distanceSquared;
        }

        if (retainedTarget == null)
            return null;

        Svc.Targets.Target = retainedTarget;
        _experimentalGrinderRsrTargetMissingSince = DateTime.MinValue;
        _experimentalGrinderMacroAnchorId = 0;
        ClearExperimentalGrinderMacroTargetCandidate();
        return retainedTarget;
    }

    private void SuppressExperimentalGrinderPullCandidate(ulong candidateId, DateTime now)
    {
        _experimentalGrinderSuppressedPullCandidateId = candidateId;
        _experimentalGrinderSuppressedPullCandidateUntil = now.AddMilliseconds(GrinderMultiPullRetrySuppressMs);
    }

    private bool IsExperimentalGrinderPullCandidateSuppressed(ulong candidateId, DateTime now)
    {
        if (_experimentalGrinderSuppressedPullCandidateId == 0)
            return false;

        if (now >= _experimentalGrinderSuppressedPullCandidateUntil)
        {
            _experimentalGrinderSuppressedPullCandidateId = 0;
            _experimentalGrinderSuppressedPullCandidateUntil = DateTime.MinValue;
            return false;
        }

        return _experimentalGrinderSuppressedPullCandidateId == candidateId;
    }

    private void SetExperimentalGrinderPullHold(string reason)
    {
        if (string.Equals(_experimentalGrinderPullHoldReason, reason, StringComparison.Ordinal))
            return;

        _experimentalGrinderPullHoldReason = reason;
    }

    private void ClearExperimentalGrinderPullHold()
        => _experimentalGrinderPullHoldReason = string.Empty;

    private static bool HasNearbyForlorn(IReadOnlyList<IBattleNpc> enemies, Vector3 playerPosition)
    {
        var radiusSquared = GrinderForlornPriorityRadius * GrinderForlornPriorityRadius;
        foreach (var enemy in enemies)
        {
            if (IsForlorn(enemy)
                && NavigationGeometry.HorizontalDistanceSquared(playerPosition, enemy.Position) <= radiusSquared)
                return true;
        }

        return false;
    }

    private static bool IsForlorn(IBattleNpc enemy)
        => enemy.NameId is ForlornNameId or ForlornMaidenNameId;

    private bool IsExperimentalGrinderMacroTargetStable(ulong targetId, DateTime now, out double remainingMs)
    {
        if (_experimentalGrinderPendingMacroTargetId != targetId)
        {
            _experimentalGrinderPendingMacroTargetId = targetId;
            _experimentalGrinderPendingMacroTargetSince = now;
            remainingMs = GrinderMacroTargetCommitMs;
            return false;
        }

        var elapsedMs = (now - _experimentalGrinderPendingMacroTargetSince).TotalMilliseconds;
        remainingMs = Math.Max(0d, GrinderMacroTargetCommitMs - elapsedMs);
        return elapsedMs >= GrinderMacroTargetCommitMs;
    }

    private void ClearExperimentalGrinderMacroTargetCandidate()
    {
        _experimentalGrinderPendingMacroTargetId = 0;
        _experimentalGrinderPendingMacroTargetSince = DateTime.MinValue;
    }

    private static bool IsLoadedExperimentalGrinderFateEnemy(ulong gameObjectId, ushort fateId)
        => FindLoadedExperimentalGrinderFateEnemy(gameObjectId, fateId) != null;

    private static IBattleNpc? FindLoadedExperimentalGrinderFateEnemy(ulong gameObjectId, ushort fateId)
    {
        if (gameObjectId == 0 || fateId == 0)
            return null;

        foreach (var obj in Svc.Objects)
        {
            if (obj.GameObjectId == gameObjectId
                && obj is IBattleNpc npc
                && FateTargeting.IsFateEnemy(npc, fateId))
                return npc;
        }

        return null;
    }

    private bool IsExperimentalGrinderCombatEligibleFate(IFate fate)
        => _fateContext.Request.Purpose == FateExecutionPurpose.GeneralGrinder
            && Service.Configuration.FateGrinder.ExperimentalCombat
            && !IsDefendFate(fate.FateId)
            && !IsEscortFate(fate.FateId)
            && !IsCollectFate(fate.FateId)
            && !FateMetadata.PrefersEventObjects(fate.FateId)
            && FateMetadata.GetPrimaryCombatTargetName(fate.FateId) == null
            && FateMetadata.GetSecondaryCombatTargetName(fate.FateId) == null
            && FateMetadata.GetFallbackSpawnerName(fate.FateId) == null;

    private bool DriveExperimentalGrinderNoObjectiveTargets(IFate fate)
    {
        if (_experimentalGrinderTargetFreelyActive || _fateRotationSolver.IsAutoDutyOwned)
            ReleaseExperimentalGrinderCombatSession("no current-FATE objective enemy is loaded", true);

        var fallbackThreat = _fateCombatTargets.SelectFallbackDirectThreat(fate.FateId, fate.Position, fate.Radius);
        if (fallbackThreat != null)
        {
            DriveFateEnemy(fate, fallbackThreat);
            return true;
        }

        _fateContext.CombatTargetId = 0;
        _fateCombatEngagement.Reset();
        ReleaseFateTacticalMovement();

        if (_fateContext.NoCombatTargetSince == DateTime.MinValue)
        {
            _fateContext.NoCombatTargetSince = DateTime.Now;
            StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Experimental grinder is waiting briefly for the next objective wave in {FateMetadata.GetName(fate.FateId)}; broad RSR AutoDuty targeting is paused while no current-FATE enemy is loaded.";
            return true;
        }

        if ((DateTime.Now - _fateContext.NoCombatTargetSince).TotalMilliseconds < FateCombatReacquireDelayMs)
        {
            StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Experimental grinder is waiting briefly for the next objective wave in {FateMetadata.GetName(fate.FateId)}; RSR remains paused until a current-FATE enemy is visible.";
            return true;
        }

        var centerDestination = ResolveReachableFateDestination(fate.Position, Math.Min(12f, Math.Max(6f, fate.Radius * 0.25f)), fate.Position.Y) ?? fate.Position;
        var centerDistanceSquared = NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, centerDestination);
        if (centerDistanceSquared > FateCombatReacquireCenterTolerance * FateCombatReacquireCenterTolerance)
        {
            EnsureFateNavigation(centerDestination, false, $"experimental grinder FATE center reacquire {fate.FateId}", FateNavigationIntent.ReacquireCenter);
            _fateContext.AutomationStatus = $"No current-FATE enemies are visible in {FateMetadata.GetName(fate.FateId)}; moving toward the encounter center with RSR paused to avoid unrelated overworld targets.";
            return true;
        }

        StopFateOwnedNavigation();
        _fateContext.AutomationStatus = $"Near the center of {FateMetadata.GetName(fate.FateId)}; waiting for the next objective enemy with RSR paused.";
        return true;
    }

    private static List<IBattleNpc> GetLoadedGrinderFateEnemies(ushort fateId)
    {
        var enemies = new List<IBattleNpc>();
        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleNpc npc && FateTargeting.IsFateEnemy(npc, fateId))
                enemies.Add(npc);
        }
        return enemies;
    }

    private static IBattleNpc? GetCurrentExperimentalGrinderTarget(ushort fateId)
        => Svc.Targets.Target is IBattleNpc current && FateTargeting.IsFateEnemy(current, fateId)
            ? current
            : null;

    private static IBattleNpc? SelectExperimentalGrinderSeedTarget(IReadOnlyList<IBattleNpc> enemies, ExperimentalGrinderCombatPolicy policy)
    {
        var player = Player.Object;
        if (player == null || enemies.Count == 0)
            return null;

        IBattleNpc? best = null;
        var bestDistance = float.MaxValue;
        uint bestMaxHp = 0;
        foreach (var enemy in enemies)
        {
            var distance = Vector3.DistanceSquared(player.Position, enemy.Position);
            if (policy == ExperimentalGrinderCombatPolicy.Boss)
            {
                if (best == null || enemy.MaxHp > bestMaxHp || (enemy.MaxHp == bestMaxHp && distance < bestDistance))
                {
                    best = enemy;
                    bestMaxHp = enemy.MaxHp;
                    bestDistance = distance;
                }
            }
            else if (best == null || distance < bestDistance)
            {
                best = enemy;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static ExperimentalGrinderCombatPolicy ClassifyExperimentalGrinderCombat(IReadOnlyList<IBattleNpc> enemies)
    {
        if (enemies.Count <= 1)
            return ExperimentalGrinderCombatPolicy.Boss;

        uint highest = 0;
        uint second = 0;
        foreach (var enemy in enemies)
        {
            var hp = enemy.MaxHp;
            if (hp > highest)
            {
                second = highest;
                highest = hp;
            }
            else if (hp > second)
            {
                second = hp;
            }
        }

        if (second == 0 || highest >= second * GrinderBossMaxHpRatio)
            return ExperimentalGrinderCombatPolicy.Boss;

        return ExperimentalGrinderCombatPolicy.GenericPack;
    }

    private enum ExperimentalGrinderCombatPolicy
    {
        GenericPack,
        Boss,
    }
}
