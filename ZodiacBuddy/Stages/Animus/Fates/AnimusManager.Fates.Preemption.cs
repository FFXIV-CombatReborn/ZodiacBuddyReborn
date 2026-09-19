using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using System;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const int FatePreemptionUnsyncRetryMs = 1500;
    private const int FatePreemptionUnsyncMaxAttempts = 3;

    private bool RequestFatePreemption(string status)
    {
        if (_fateAutomation.IsTerminal)
            return true;
        if (!_fateContext.Request.IsFiller)
            return false;
        if (_fateContext.FinishFillerBeforeYield)
            return false;
        if (_fateContext.AutomationState == FateAutomationState.ClearingAggro
            && _fateContext.PostCombatOutcome == FatePostCombatOutcome.Preempted)
            return false;
        if (_fateContext.PreemptionPending
            || _fateContext.AutomationState is FateAutomationState.PreemptionUnsync or FateAutomationState.PreemptionEgress)
            return false;

        StopFateOwnedNavigation();
        _advancedUnstuck.Cancel();
        StopUnstuckMonitoring();
        _restartNavigation = null;
        _fatePreemptionEgress.Reset();
        ReleaseFateTacticalMovement();
        RequestFateRotationSolverStop("preempting an experimental filler for higher-priority FATE work");
        _fateCombatTargets.Reset();
        _fateCombatEngagement.Reset();
        ClearFatePreemptionTarget();
        _fateContext.CombatTargetId = 0;
        _fateContext.IncidentalAggroTargetId = 0;
        _fateContext.InteractionTargetId = 0;
        _fateContext.StarterObjectId = 0;
        _fateContext.EscortAnchorId = 0;
        _fateContext.DefendAnchorId = 0;
        _fateContext.NavigationActive = false;
        _fateContext.NavigationDestination = null;
        _fateContext.NavigationIntent = FateNavigationIntent.None;
        _fateContext.PostCombatOutcome = FatePostCombatOutcome.None;
        _fateContext.DeferredTerminalStatus = string.Empty;

        var fillerId = _fateContext.Request.FateId;
        if (!TryGetFate(fillerId, out var filler)
            || filler.Position == Vector3.Zero
            || filler.Radius <= 0f)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Cannot safely egress filler {fillerId}; live geometry is unavailable.");
            if (!Svc.Condition[ConditionFlag.InCombat])
            {
                PreemptFateAutomation(status);
                return true;
            }

            _fateContext.ResidualAggroOrigin = Player.Object?.Position;
            _fateContext.ResidualLoggedTargetId = 0;
            ResetResidualAggroDiagnostics();
            _fateContext.PostCombatOutcome = FatePostCombatOutcome.Preempted;
            _fateContext.DeferredTerminalStatus = $"{status} Live filler geometry vanished before egress; residual combat was cleared before handoff.";
            _fateContext.AutomationState = FateAutomationState.ClearingAggro;
            _fateContext.AutomationStatus = "Required FATE work is waiting; filler geometry vanished, so ZBR is clearing only blocking combat before handoff.";
            return false;
        }

        _fateContext.PreemptionPending = true;
        _fateContext.PreemptionReason = status;
        _fateContext.PreemptionFateId = fillerId;
        _fateContext.PreemptionFateCenter = filler.Position;
        _fateContext.PreemptionFateRadius = filler.Radius;
        _fateContext.PreemptionUnsyncAttempts = 0;
        _fateContext.PreemptionUnsyncNextAttemptAt = DateTime.MinValue;
        _fateContext.AutomationState = FateAutomationState.PreemptionUnsync;
        _fateContext.AutomationStatus = $"Required FATE work became actionable; abandoning filler {FateMetadata.GetName(fillerId)} and dropping its level sync before egress.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Preempting filler {fillerId}; preparing unsync and safe egress.");
        return false;
    }

    private void TickFatePreemption()
    {
        if (!_fateContext.PreemptionPending)
            return;

        switch (_fateContext.AutomationState)
        {
            case FateAutomationState.PreemptionUnsync:
                TickFatePreemptionUnsync();
                return;
            case FateAutomationState.PreemptionEgress:
                TickFatePreemptionEgress();
                return;
        }
    }

    private unsafe void TickFatePreemptionUnsync()
    {
        var player = Player.Object;
        if (player == null)
            return;

        ReleaseFateTacticalMovement();
        ClearFatePreemptionTarget();

        if (_fateRotationSolver.IsOwned || _fateRotationSolver.HasPendingStop)
        {
            _fateContext.AutomationStatus = "Waiting for RotationSolverReborn to stop completely before dropping filler sync.";
            return;
        }

        var manager = FateManager.Instance();
        if (manager == null)
        {
            FallBackToFinishPreemptedFiller("FateManager became unavailable while dropping filler sync.");
            return;
        }

        var fillerId = _fateContext.PreemptionFateId;
        if (manager->SyncedFateId == fillerId)
        {
            var now = DateTime.Now;
            if (now < _fateContext.PreemptionUnsyncNextAttemptAt)
                return;

            if (_fateContext.PreemptionUnsyncAttempts >= FatePreemptionUnsyncMaxAttempts)
            {
                FallBackToFinishPreemptedFiller($"Level sync for filler {fillerId} remained active after {FatePreemptionUnsyncMaxAttempts} unsync request(s).");
                return;
            }

            if (!CanAct || Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Casting87])
            {
                _fateContext.AutomationStatus = $"Waiting until actions are available before dropping level sync for filler {FateMetadata.GetName(fillerId)} ({fillerId}).";
                return;
            }

            ++_fateContext.PreemptionUnsyncAttempts;
            _fateContext.PreemptionUnsyncNextAttemptAt = now.AddMilliseconds(FatePreemptionUnsyncRetryMs);
            manager->LevelSync();
            _fateContext.AutomationStatus = $"Dropping level sync for filler {FateMetadata.GetName(fillerId)} ({fillerId}) before escape (attempt {_fateContext.PreemptionUnsyncAttempts}/{FatePreemptionUnsyncMaxAttempts}).";
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Unsync requested for filler {fillerId}, attempt {_fateContext.PreemptionUnsyncAttempts}/{FatePreemptionUnsyncMaxAttempts}.");
            return;
        }

        if (!Svc.Condition[ConditionFlag.InCombat])
        {
            CompleteSafeFillerPreemption("Filler sync is clear and the player is not in combat; no escape path is required.");
            return;
        }

        _fatePreemptionEgress.Begin(_fateContext.PreemptionFateCenter, _fateContext.PreemptionFateRadius);
        _fateContext.AutomationState = FateAutomationState.PreemptionEgress;
        _fateContext.AutomationStatus = $"Filler sync is clear; vnav is escaping {FateMetadata.GetName(fillerId)} while combat automation remains disabled.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Filler {fillerId} unsynced; beginning safe egress.");
    }

    private void TickFatePreemptionEgress()
    {
        var player = Player.Object;
        if (player == null)
            return;

        ReleaseFateTacticalMovement();
        ClearFatePreemptionTarget();

        var outcome = _fatePreemptionEgress.Tick(player.Position, Svc.Condition[ConditionFlag.InCombat]);
        if (outcome == FateEgressOutcome.Safe)
        {
            CompleteSafeFillerPreemption("vnav reached safe ground outside the filler FATE and combat cleared.");
            return;
        }

        if (outcome == FateEgressOutcome.Failed)
        {
            FallBackToFinishPreemptedFiller(_fatePreemptionEgress.Status);
            return;
        }

        _fateContext.AutomationStatus = $"Required FATE work is waiting. {_fatePreemptionEgress.Status} Combat automation remains disabled until escape completes.";
    }

    private void CompleteSafeFillerPreemption(string detail)
    {
        var reason = _fateContext.PreemptionReason;
        var fillerId = _fateContext.PreemptionFateId;
        _fatePreemptionEgress.Reset();
        ClearFatePreemptionState();
        var status = string.IsNullOrWhiteSpace(reason)
            ? $"Experimental filler {FateMetadata.GetName(fillerId)} ({fillerId}) preempted after safe egress."
            : $"{reason} {detail}";
        PreemptFateAutomation(status);
    }

    private void FallBackToFinishPreemptedFiller(string reason)
    {
        var fillerId = _fateContext.PreemptionFateId != 0 ? _fateContext.PreemptionFateId : _fateContext.Request.FateId;
        _fatePreemptionEgress.Reset();
        StopFateOwnedNavigation();
        ReleaseFateTacticalMovement();
        RequestFateRotationSolverStop("filler egress failed; returning control to the normal filler executor");
        ClearFatePreemptionTarget();
        ClearFatePreemptionState();

        if (TryGetFate(fillerId, out var filler)
            && filler.State is DalamudFateState.Running or DalamudFateState.Preparing)
        {
            _fateContext.FinishFillerBeforeYield = true;
            _fateObstacleMaps.Reset();
            _fateContext.WorkingFateId = 0;
            _fateContext.WorkingFateIsPrerequisite = false;
            _fateContext.WorkingFateObservedPreparing = false;
            _fateContext.WorkingFateObservedRunning = false;
            _fateContext.WorkingFateCompletionObserved = false;
            _fateContext.WorkingFateMissingSince = DateTime.MinValue;
            _fateContext.CombatTargetId = 0;
            _fateContext.IncidentalAggroTargetId = 0;
            _fateContext.InteractionTargetId = 0;
            _fateContext.NavigationActive = false;
            _fateContext.NavigationDestination = null;
            _fateContext.NavigationIntent = FateNavigationIntent.None;
            _fateContext.FillerStartDeadline = DateTime.MinValue;
            _fateContext.AutomationState = FateAutomationState.Resolving;
            _fateContext.AutomationStatus = $"Could not safely escape filler {FateMetadata.GetName(fillerId)} ({fillerId}); finishing it before yielding to required FATE work. {reason}";
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Egress failed for filler {fillerId}; resuming it: {reason}");
            return;
        }

        if (!Svc.Condition[ConditionFlag.InCombat])
        {
            PreemptFateAutomation($"Filler {FateMetadata.GetName(fillerId)} ({fillerId}) disappeared while egress fallback was being prepared; combat is already clear, so required FATE work may continue.");
            return;
        }

        _fateContext.ResidualAggroOrigin = Player.Object?.Position;
        _fateContext.ResidualLoggedTargetId = 0;
        ResetResidualAggroDiagnostics();
        _fateContext.PostCombatOutcome = FatePostCombatOutcome.Preempted;
        _fateContext.DeferredTerminalStatus = $"Filler {FateMetadata.GetName(fillerId)} ({fillerId}) vanished during preemption; blocking combat was cleared before handoff.";
        _fateContext.AutomationState = FateAutomationState.ClearingAggro;
        _fateContext.AutomationStatus = "The filler ended during escape, but combat remains; clearing only blocking attackers before required FATE travel.";
        Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Filler {fillerId} vanished during egress; clearing residual combat.");
    }

    private void ClearFatePreemptionState()
    {
        _fateContext.PreemptionPending = false;
        _fateContext.PreemptionReason = string.Empty;
        _fateContext.PreemptionFateId = 0;
        _fateContext.PreemptionFateCenter = default;
        _fateContext.PreemptionFateRadius = 0f;
        _fateContext.PreemptionUnsyncNextAttemptAt = DateTime.MinValue;
        _fateContext.PreemptionUnsyncAttempts = 0;
    }

    private static unsafe void ClearFatePreemptionTarget()
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem != null)
            targetSystem->Target = null;
    }
}
