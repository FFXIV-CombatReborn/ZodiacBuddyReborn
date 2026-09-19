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
    private unsafe void TickFateClearingAggro()
    {
        ReleaseFateTacticalMovement();
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            var player = Player.Object;
            if (player == null)
                return;

            _fateContext.ResidualAggroOrigin ??= player.Position;
            IBattleNpc? target;
            bool allowPursuit;
            int ignoredFormerFateEnemies;
            int formerStillTagged;
            if (_fateContext.PostCombatOutcome == FatePostCombatOutcome.Preempted)
            {
                target = FateTargeting.GetBestDirectThreatTarget(_fateContext.CombatTargetId, out _, out _, out _);
                allowPursuit = target != null
                    && Vector3.DistanceSquared(player.Position, target.Position) <= FatePreemptionAggroLeashDistance * FatePreemptionAggroLeashDistance
                    && Vector3.DistanceSquared(_fateContext.ResidualAggroOrigin.Value, target.Position) <= FatePreemptionAggroLeashDistance * FatePreemptionAggroLeashDistance;
                if (!allowPursuit)
                    target = null;
                ignoredFormerFateEnemies = 0;
                formerStillTagged = 0;
            }
            else
            {
                target = FateTargeting.GetBestResidualAggroTarget(
                    _fateContext.WorkingFateId,
                    _fateContext.KnownCombatEnemyIds,
                    _fateContext.CombatTargetId,
                    _fateContext.ResidualAggroOrigin.Value,
                    FateResidualAggroLeashDistance,
                    FateResidualAggroAcquireDistance,
                    out allowPursuit,
                    out ignoredFormerFateEnemies);
                formerStillTagged = FateTargeting.CountKnownEnemiesStillTagged(_fateContext.WorkingFateId, _fateContext.KnownCombatEnemyIds);
            }

            if (target == null)
            {
                if (_fateContext.NavigationIntent == FateNavigationIntent.ResidualAggro)
                    StopFateOwnedNavigation();
                LogResidualAggroTargetLost(ignoredFormerFateEnemies, formerStillTagged);
                ClearResidualCombatTarget();
                _fateContext.ResidualLoggedTargetId = 0;
                RequestFateRotationSolverStop("waiting for a valid residual aggro target");
                _fateContext.AutomationStatus = _fateContext.PostCombatOutcome == FatePostCombatOutcome.Preempted
                    ? "Yielding filler combat; waiting for remaining aggro to clear without chasing filler actors."
                    : ignoredFormerFateEnemies > 0
                        ? $"Waiting for {ignoredFormerFateEnemies} former FATE mob(s) to disengage without pursuing them."
                        : "Waiting for residual combat to clear before resuming FATE automation.";
                if (ignoredFormerFateEnemies > 0 && EzThrottler.Throttle($"ZBR_FateResidualFormer_{_fateContext.WorkingFateId}", 2000))
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Ignoring {ignoredFormerFateEnemies} departing former FATE mob(s); stillTaggedWithEndedFate={formerStillTagged}.");
                return;
            }

            if (_fateContext.CombatTargetId != 0 && _fateContext.CombatTargetId != target.GameObjectId
                && _fateContext.NavigationIntent == FateNavigationIntent.ResidualAggro)
                StopFateOwnedNavigation();

            _fateContext.CombatTargetId = target.GameObjectId;
            TargetSystem.Instance()->Target = (GameObject*)target.Address;
            StartFateRotationSolver();
            LogResidualAggroDiagnostic(target, allowPursuit, ignoredFormerFateEnemies, formerStillTagged);

            if (_fateContext.ResidualLoggedTargetId != target.GameObjectId)
            {
                _fateContext.ResidualLoggedTargetId = target.GameObjectId;
                Service.PluginLog.Verbose(
                    $"[ZodiacBuddy/FATE] Residual aggro target '{target.Name.TextValue}' id={target.GameObjectId} fateId={FateTargeting.GetFateId(target)} " +
                    $"knownFormerFate={_fateContext.KnownCombatEnemyIds.Contains(target.GameObjectId)} pursuit={allowPursuit} ignoredFormer={ignoredFormerFateEnemies} formerStillTagged={formerStillTagged}.");
            }

            var distance = Vector3.Distance(player.Position, target.Position);
            if (allowPursuit && distance > FateCombatEngageDistance)
            {
                EnsureResidualAggroNavigation(target);
                _fateContext.AutomationStatus = $"Closing on residual attacker {target.Name.TextValue} ({distance:F1}y).";
                return;
            }

            if (_fateContext.NavigationIntent == FateNavigationIntent.ResidualAggro)
                StopFateOwnedNavigation();
            _fateContext.AutomationStatus = allowPursuit
                ? $"Clearing residual attacker {target.Name.TextValue}."
                : $"Holding position while former FATE enemy {target.Name.TextValue} disengages.";
            return;
        }

        if (_fateContext.NavigationIntent == FateNavigationIntent.ResidualAggro)
            StopFateOwnedNavigation();
        ClearResidualCombatTarget();
        RequestFateRotationSolverStop("residual FATE combat ended");

        if (!_fateContext.Request.IsFiller
            && _fateContext.WorkingFateId != 0
            && TryGetFate(_fateContext.WorkingFateId, out var settlingFate)
            && settlingFate.State == DalamudFateState.Ending)
        {
            _fateContext.AutomationStatus = $"{FateMetadata.GetName(_fateContext.WorkingFateId)} residual combat is clear; waiting for the FATE to finalize.";
            return;
        }

        _fateContext.ResidualAggroOrigin = null;
        _fateContext.ResidualLoggedTargetId = 0;
        ResetResidualAggroDiagnostics();
        var outcome = _fateContext.PostCombatOutcome;
        _fateContext.PostCombatOutcome = FatePostCombatOutcome.None;
        ApplyFatePostCombatOutcome(outcome);
    }
    private unsafe void ClearResidualCombatTarget()
    {
        var targetId = _fateContext.CombatTargetId;
        if (targetId != 0 && Svc.Targets.Target?.GameObjectId == targetId)
            TargetSystem.Instance()->Target = (GameObject*)0;
        _fateContext.CombatTargetId = 0;
    }

    private unsafe void LogResidualAggroDiagnostic(IBattleNpc target, bool allowPursuit, int ignoredFormerFateEnemies, int formerStillTagged)
    {
        var player = Player.Object;
        if (player == null)
            return;

        var now = DateTime.Now;
        var fateId = FateTargeting.GetFateId(target);
        var targetChanged = _fateContext.ResidualDiagnosticTargetId != target.GameObjectId;
        var fateChanged = !targetChanged && _fateContext.ResidualDiagnosticFateId != fateId;
        if (!targetChanged && !fateChanged && now < _fateContext.ResidualDiagnosticNextAt)
            return;

        var previousFateId = _fateContext.ResidualDiagnosticFateId;
        _fateContext.ResidualDiagnosticTargetId = target.GameObjectId;
        _fateContext.ResidualDiagnosticFateId = fateId;
        _fateContext.ResidualDiagnosticNextAt = now.AddSeconds(2);

        var manager = FateManager.Instance();
        var playerFateId = manager == null ? (ushort)0 : manager->GetCurrentFateId();
        var syncedFateId = manager == null ? (ushort)0 : manager->SyncedFateId;
        var hardTargetId = Svc.Targets.Target?.GameObjectId ?? 0;
        var distance = Vector3.Distance(player.Position, target.Position);
        var originDistance = _fateContext.ResidualAggroOrigin is Vector3 origin ? Vector3.Distance(origin, target.Position) : -1f;
        var transition = targetChanged
            ? "new-target"
            : fateChanged
                ? $"fate-{previousFateId}->{fateId}"
                : "heartbeat";

        Service.PluginLog.Verbose(
            $"[ZodiacBuddy/FATE] Residual diagnostic transition={transition} name='{target.Name.TextValue}' id={target.GameObjectId} baseId={target.BaseId} nameId={target.NameId} " +
            $"fateId={fateId} knownFormer={_fateContext.KnownCombatEnemyIds.Contains(target.GameObjectId)} distance={distance:F1}y originDistance={originDistance:F1}y " +
            $"hp={target.CurrentHp}/{target.MaxHp} targetable={target.IsTargetable} dead={target.IsDead} battleNpcKind={target.BattleNpcKind} hostile={FateTargeting.IsHostileEnemy(target)} attackable={FateTargeting.IsAttackableEnemy(target)} " +
            $"targetObjectId={target.TargetObjectId} targetingPlayer={target.TargetObjectId == player.GameObjectId} hardTargetId={hardTargetId} hardTargetMatches={hardTargetId == target.GameObjectId} " +
            $"playerFateId={playerFateId} syncedFateId={syncedFateId} inCombat={Svc.Condition[ConditionFlag.InCombat]} rsrOwned={_fateRotationSolver.IsOwned} rsrLoaded={RSRIPC.IsLoaded} " +
            $"allowPursuit={allowPursuit} navIntent={_fateContext.NavigationIntent} navActive={_fateContext.NavigationActive} ignoredFormer={ignoredFormerFateEnemies} formerStillTagged={formerStillTagged} nameplateIcon={FateTargeting.GetNameplateIconId(target)}.");
    }
    private unsafe void LogResidualAggroTargetLost(int ignoredFormerFateEnemies, int formerStillTagged)
    {
        if (_fateContext.ResidualDiagnosticTargetId == 0)
            return;

        var manager = FateManager.Instance();
        var playerFateId = manager == null ? (ushort)0 : manager->GetCurrentFateId();
        var syncedFateId = manager == null ? (ushort)0 : manager->SyncedFateId;
        var hardTargetId = Svc.Targets.Target?.GameObjectId ?? 0;
        Service.PluginLog.Verbose(
            $"[ZodiacBuddy/FATE] Residual diagnostic transition=target-lost previousId={_fateContext.ResidualDiagnosticTargetId} previousFateId={_fateContext.ResidualDiagnosticFateId} " +
            $"hardTargetId={hardTargetId} playerFateId={playerFateId} syncedFateId={syncedFateId} inCombat={Svc.Condition[ConditionFlag.InCombat]} " +
            $"rsrOwned={_fateRotationSolver.IsOwned} rsrLoaded={RSRIPC.IsLoaded} ignoredFormer={ignoredFormerFateEnemies} formerStillTagged={formerStillTagged}.");
        ResetResidualAggroDiagnostics();
    }
    private void ResetResidualAggroDiagnostics()
        => _fateAutomation.ResetResidualDiagnostics();
    private void EnsureResidualAggroNavigation(IBattleNpc target)
    {
        if (!_automationRun.IsActive(_fateContext.AutomationRunId))
            return;

        var destination = target.Position;
        if (_fateContext.NavigationActive
            && _fateContext.NavigationIntent == FateNavigationIntent.ResidualAggro
            && _fateContext.NavigationDestination is Vector3 current
            && NavigationGeometry.HorizontalDistanceSquared(current, destination) < 4f * 4f)
            return;

        if (_fateContext.NavigationActive)
            StopFateOwnedNavigation();

        _fateContext.NavigationDestination = destination;
        _fateContext.NavigationIntent = FateNavigationIntent.ResidualAggro;
        _fateContext.NavigationFly = false;
        _fateContext.NavigationActive = true;
        var accepted = StartSimpleMove(destination, false, HandleFateNavigationResult, NavigationPurpose.FateTravel, NavigationRestartPolicy.WalkAfterUnstuck, ResolveFateNavigationDestination);
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Navigation intent=ResidualAggro destination={destination} fly=False accepted={accepted} reason=residual attacker {target.GameObjectId}.");
        if (!accepted)
        {
            _fateContext.NavigationActive = false;
            _fateContext.NavigationDestination = null;
            _fateContext.NavigationIntent = FateNavigationIntent.None;
        }
    }
    private void ApplyFatePostCombatOutcome(FatePostCombatOutcome outcome)
    {
        switch (outcome)
        {
            case FatePostCombatOutcome.PrerequisiteComplete:
                FinishPrerequisiteFate();
                return;
            case FatePostCombatOutcome.PrerequisiteRetry:
                ResetPrerequisiteAfterUnconfirmedEnd();
                return;
            case FatePostCombatOutcome.TargetComplete:
                FinishTargetFate();
                return;
            case FatePostCombatOutcome.TargetFailed:
                FinishFateAutomation(FateAutomationState.Failed, FateExecutionResultKind.EndedOrFailed, $"FATE {_fateContext.Request.Name} disappeared without completion evidence.");
                return;
            case FatePostCombatOutcome.Preempted:
                var status = string.IsNullOrWhiteSpace(_fateContext.DeferredTerminalStatus)
                    ? "Experimental filler preempted after immediate combat cleared."
                    : _fateContext.DeferredTerminalStatus;
                FinishFateAutomation(FateAutomationState.Preempted, FateExecutionResultKind.Preempted, status);
                return;
            default:
                _fateContext.AutomationState = FateAutomationState.Resolving;
                return;
        }
    }
    private void ResetPrerequisiteAfterUnconfirmedEnd()
    {
        var prerequisite = _fateContext.WorkingFateId;
        _fateObstacleMaps.Reset();
        _fateContext.WorkingFateId = 0;
        _fateContext.WorkingFateIsPrerequisite = false;
        _fateContext.WorkingFateObservedPreparing = false;
        _fateContext.WorkingFateObservedRunning = false;
        _fateContext.WorkingFateCompletionObserved = false;
        _fateContext.WorkingFateMissingSince = DateTime.MinValue;
        _fateContext.AutomationState = FateAutomationState.Resolving;
        _fateContext.AutomationStatus = $"Prerequisite {FateMetadata.GetName(prerequisite)} ended without completion evidence; waiting for another attempt.";
        Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Prerequisite FateId={prerequisite} disappeared without reaching 100%/Ending; it will not be treated as completed.");
    }
    private void FinishPrerequisiteFate()
    {
        var completedPrerequisite = _fateContext.WorkingFateId;
        _fateContext.PrerequisiteDone = true;
        _fateObstacleMaps.Reset();
        _fateContext.WorkingFateId = 0;
        _fateContext.WorkingFateIsPrerequisite = false;
        _fateContext.WorkingFateObservedPreparing = false;
        _fateContext.WorkingFateObservedRunning = false;
        _fateContext.WorkingFateCompletionObserved = false;
        _fateContext.WorkingFateMissingSince = DateTime.MinValue;
        _fateContext.AutomationState = FateAutomationState.Resolving;
        _fateContext.AutomationStatus = $"Prerequisite {FateMetadata.GetName(completedPrerequisite)} ended; waiting for {_fateContext.Request.Name}.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Prerequisite FateId={completedPrerequisite} ended; target FateId={_fateContext.Request.FateId} remains authoritative.");
    }
    private void FinishTargetFate()
    {
        _fateContext.WorkingFateId = 0;
        _fateContext.WorkingFateIsPrerequisite = false;
        var completionObserved = _fateContext.WorkingFateCompletionObserved;
        _fateContext.WorkingFateObservedPreparing = false;
        _fateContext.WorkingFateObservedRunning = false;
        _fateContext.WorkingFateCompletionObserved = false;
        _fateContext.WorkingFateMissingSince = DateTime.MinValue;
        RequestFateRotationSolverStop("the target FATE ended");

        if (!_fateContext.HasBookTarget)
        {
            if (_fateContext.TargetObservedRunning && _fateContext.TargetParticipated && completionObserved)
                CompleteFateAutomation($"FATE {_fateContext.Request.Name} ended after ZBR entered and participated in it.");
            else
                FinishFateAutomation(FateAutomationState.Failed, FateExecutionResultKind.EndedOrFailed, $"FATE {_fateContext.Request.Name} ended without confirmed participation/completion evidence.");
            return;
        }

        _fateContext.RuntimeResult = new FateExecutionResult(
            FateExecutionResultKind.Completed,
            _fateContext.Request.FateId,
            $"Runtime completion confirmed for {_fateContext.Request.Name}; awaiting exact book credit.",
            DateTime.Now);
        _fateContext.AutomationState = FateAutomationState.AwaitingExternalConfirmation;
        _fateContext.CreditDeadline = DateTime.Now.AddSeconds(FateBookCreditWaitSeconds);
        _fateContext.AutomationStatus = $"{_fateContext.Request.Name} ended; runtime completion confirmed, waiting for the book slot to credit.";
    }
    private void TickFateAwaitingExternalConfirmation()
    {
        if (IsActiveBookFateComplete())
        {
            CompleteFateAutomation($"Book credit confirmed for {_fateContext.Request.Name}.");
            return;
        }

        if (DateTime.Now >= _fateContext.CreditDeadline)
            RetryFateLater($"{_fateContext.Request.Name} ended but book FATE slot {_fateContext.BookTarget.FateSlot + 1} did not credit within {FateBookCreditWaitSeconds} seconds; continuing the book and retrying this FATE on a later sweep.");
    }
    private unsafe bool IsActiveBookFateComplete()
    {
        if (!_fateContext.HasBookTarget || _fateContext.BookId == 0 || _fateContext.BookTarget.FateSlot is < 0 or > 2)
            return false;

        var relicNote = RelicNote.Instance();
        return relicNote != null
            && relicNote->RelicNoteId == _fateContext.BookId
            && relicNote->IsFateComplete(_fateContext.BookTarget.FateSlot);
    }
}
