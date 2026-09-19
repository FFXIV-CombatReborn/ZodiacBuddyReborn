using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using ZodiacBuddy.Stages.Animus.Data;

namespace ZodiacBuddy.Stages.Animus;

internal enum DungeonAutomationState
{
    Idle,
    AwaitingCompletion,
    AwaitingReturn,
    Complete,
    Failed,
    Cancelled,
}

internal partial class AnimusManager
{
    private DungeonObjectiveDefinition _dungeonAutomationTarget;
    private uint _dungeonBookId;
    private DungeonAutomationState _dungeonAutomationState = DungeonAutomationState.Idle;
    private string _dungeonAutomationStatus = "Idle.";
    private const int DungeonBookCreditWaitSeconds = 10;
    private bool _dungeonAutoDutyStarted;
    private bool _dungeonEnvironmentObserved;
    private DateTime _dungeonCreditDeadline = DateTime.MinValue;

    private unsafe bool StartDungeonAutomation(DungeonObjectiveDefinition target, uint bookId, bool requireAutoDuty)
    {
        if (bookId == 0 || target.DungeonSlot is < 0 or > 2 || target.ContentsFinderConditionId == 0)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/DUNGEON] Invalid book context for '{target.Name}': book={bookId}, slot={target.DungeonSlot}, cfc={target.ContentsFinderConditionId}.");
            return false;
        }

        var relicNote = RelicNote.Instance();
        if (relicNote == null || relicNote->RelicNoteId != bookId)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/DUNGEON] Active book changed before '{target.Name}' could start.");
            return false;
        }

        if (relicNote->IsDungeonComplete(target.DungeonSlot))
        {
            Service.Plugin.PrintStepProgress($"{target.Name} is already complete in this book.");
            return false;
        }

        ResetRunStateForNewCycle();
        _dungeonAutomationTarget = target;
        _dungeonBookId = bookId;
        _dungeonAutomationState = DungeonAutomationState.AwaitingCompletion;
        _dungeonAutomationStatus = $"Waiting for book dungeon slot {target.DungeonSlot + 1} to complete.";
        _dungeonAutoDutyStarted = false;
        _dungeonEnvironmentObserved = false;
        _dungeonCreditDeadline = DateTime.MinValue;

        var territoryId = target.Position.TerritoryType.RowId;
        var autoDutyEnabled = AutoDutyIpc.Enabled;
        var autoDutyHasPath = autoDutyEnabled && AutoDutyIpc.HasPath(territoryId);
        Service.PluginLog.Verbose($"[ZodiacBuddy/DUNGEON] Starting '{target.Name}' book={bookId} slot={target.DungeonSlot} cfc={target.ContentsFinderConditionId} territory={territoryId} requireAutoDuty={requireAutoDuty} autoDutyEnabled={autoDutyEnabled} hasPath={autoDutyHasPath}.");
        if (requireAutoDuty && !autoDutyHasPath)
        {
            FailDungeonAutomation($"AutoDuty does not have an available path for {target.Name}.");
            return false;
        }

        if (autoDutyHasPath)
            _dungeonAutoDutyStarted = AutoDutyIpc.StartInstance(territoryId, AutoDutyIpc.DutyMode.UnsyncRegular, useBareMode: true);

        if (_dungeonAutoDutyStarted)
        {
            _dungeonAutomationStatus = $"AutoDuty started {target.Name}; waiting for book dungeon slot {target.DungeonSlot + 1} to credit.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/DUNGEON] AutoDuty accepted start for '{target.Name}' territory={territoryId}; awaiting duty entry and authoritative book credit.");
            AttachDungeonAutomationUpdate();
            Service.Plugin.PrintStepProgress($"AutoDuty started {target.Name} unsynced.");
            return true;
        }

        if (requireAutoDuty)
        {
            FailDungeonAutomation($"AutoDuty could not start {target.Name}.");
            return false;
        }

        AgentContentsFinder.Instance()->OpenRegularDuty(target.ContentsFinderConditionId);
        _dungeonAutomationStatus = $"Duty Finder opened for {target.Name}; waiting for book dungeon slot {target.DungeonSlot + 1} to credit.";
        AttachDungeonAutomationUpdate();
        Service.Plugin.PrintStepProgress($"AutoDuty unavailable. Opened Duty Finder for {target.Name}.");
        return true;
    }

    private void AttachDungeonAutomationUpdate()
    {
        Svc.Framework.Update -= TickDungeonAutomation;
        Svc.Framework.Update += TickDungeonAutomation;
    }

    private unsafe void TickDungeonAutomation(IFramework framework)
    {
        if (_dungeonAutomationState is not (DungeonAutomationState.AwaitingCompletion or DungeonAutomationState.AwaitingReturn))
        {
            Svc.Framework.Update -= TickDungeonAutomation;
            return;
        }

        var dungeonEnvironmentActive = IsDungeonEnvironmentActive();
        if (dungeonEnvironmentActive && !_dungeonEnvironmentObserved)
        {
            _dungeonEnvironmentObserved = true;
            Service.PluginLog.Verbose($"[ZodiacBuddy/DUNGEON] Entered duty environment for '{_dungeonAutomationTarget.Name}' territory={Service.ClientState.TerritoryType}; waiting for authoritative book credit.");
        }

        if (IsActiveBookDungeonComplete())
        {
            if (dungeonEnvironmentActive)
            {
                if (_dungeonAutomationState != DungeonAutomationState.AwaitingReturn)
                    Service.PluginLog.Verbose($"[ZodiacBuddy/DUNGEON] Authoritative book credit confirmed for '{_dungeonAutomationTarget.Name}' slot={_dungeonAutomationTarget.DungeonSlot} while still inside the duty; waiting for exit.");
                _dungeonAutomationState = DungeonAutomationState.AwaitingReturn;
                _dungeonAutomationStatus = $"Book credit confirmed for {_dungeonAutomationTarget.Name}; waiting to leave the duty before continuing.";
                return;
            }

            CompleteDungeonAutomation();
            return;
        }

        if (dungeonEnvironmentActive)
            return;

        var relicNote = RelicNote.Instance();
        if (relicNote != null && relicNote->RelicNoteId != _dungeonBookId)
        {
            FailDungeonAutomation($"The active book changed before {_dungeonAutomationTarget.Name} received dungeon credit.");
            return;
        }

        if (!_dungeonAutoDutyStarted || !AutoDutyIpc.IsStopped())
        {
            _dungeonCreditDeadline = DateTime.MinValue;
            return;
        }

        if (_dungeonCreditDeadline == DateTime.MinValue)
        {
            _dungeonCreditDeadline = DateTime.Now.AddSeconds(DungeonBookCreditWaitSeconds);
            _dungeonAutomationStatus = $"AutoDuty stopped; waiting for book dungeon slot {_dungeonAutomationTarget.DungeonSlot + 1} to credit.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/DUNGEON] AutoDuty stopped for '{_dungeonAutomationTarget.Name}' outside the duty; waiting up to {DungeonBookCreditWaitSeconds}s for authoritative book credit.");
            return;
        }

        if (DateTime.Now >= _dungeonCreditDeadline)
            FailDungeonAutomation($"AutoDuty stopped, but {_dungeonAutomationTarget.Name} did not receive book credit within {DungeonBookCreditWaitSeconds} seconds.");
    }

    private unsafe bool IsActiveBookDungeonComplete()
    {
        if (_dungeonBookId == 0 || _dungeonAutomationTarget.DungeonSlot is < 0 or > 2)
            return false;

        var relicNote = RelicNote.Instance();
        return relicNote != null
            && relicNote->RelicNoteId == _dungeonBookId
            && relicNote->IsDungeonComplete(_dungeonAutomationTarget.DungeonSlot);
    }

    private bool IsDungeonEnvironmentActive()
    {
        if (_dungeonAutomationTarget.ContentsFinderConditionId == 0)
            return false;

        return Svc.Condition[ConditionFlag.BetweenAreas]
            || Svc.Condition[ConditionFlag.BetweenAreas51]
            || Svc.Condition[ConditionFlag.BoundByDuty]
            || Service.ClientState.TerritoryType == _dungeonAutomationTarget.Position.TerritoryType.RowId;
    }

    private void CompleteDungeonAutomation()
    {
        Svc.Framework.Update -= TickDungeonAutomation;
        _dungeonAutomationState = DungeonAutomationState.Complete;
        _dungeonAutomationStatus = $"Book credit confirmed for {_dungeonAutomationTarget.Name}.";
        _dungeonAutoDutyStarted = false;
        _dungeonEnvironmentObserved = false;
        _dungeonCreditDeadline = DateTime.MinValue;
        Service.PluginLog.Verbose($"[ZodiacBuddy/DUNGEON] {_dungeonAutomationStatus}");
        Service.Plugin.PrintStepProgress(_dungeonAutomationStatus);
    }

    private void FailDungeonAutomation(string message)
    {
        Svc.Framework.Update -= TickDungeonAutomation;
        _dungeonAutomationState = DungeonAutomationState.Failed;
        _dungeonAutomationStatus = message;
        _dungeonAutoDutyStarted = false;
        _dungeonEnvironmentObserved = false;
        _dungeonCreditDeadline = DateTime.MinValue;
        Service.PluginLog.Warning($"[ZodiacBuddy/DUNGEON] {message}");
    }

    private void StopDungeonAutomationState(bool cancelled)
    {
        Svc.Framework.Update -= TickDungeonAutomation;
        if (cancelled && (_dungeonAutomationState == DungeonAutomationState.AwaitingCompletion || _dungeonAutomationState == DungeonAutomationState.AwaitingReturn))
        {
            _dungeonAutomationState = DungeonAutomationState.Cancelled;
            _dungeonAutomationStatus = "Dungeon automation cancelled.";
        }

        _dungeonAutomationTarget = default;
        _dungeonBookId = 0;
        _dungeonAutoDutyStarted = false;
        _dungeonEnvironmentObserved = false;
        _dungeonCreditDeadline = DateTime.MinValue;
    }
}
