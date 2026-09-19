using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal enum BookAutomationState
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}

internal enum BookObjectiveKind
{
    None,
    Enemy,
    Dungeon,
    Fate,
    Leve,
}

internal readonly record struct BookAutomationSnapshot(
    BookAutomationState State,
    uint BookId,
    string BookName,
    BookObjectiveKind CurrentObjectiveKind,
    string CurrentObjective,
    string Status,
    int CompletedObjectives,
    int TotalObjectives);

internal partial class AnimusManager
{
    private const int BookEnemyBatchSize = 2;
    private const int BookFateTerritoryDwellSeconds = 600;

    private readonly BookAutomationCoordinator _bookCoordinator = new(BookEnemyBatchSize, BookFateTerritoryDwellSeconds);
    private BookPlannerState _bookState => _bookCoordinator.State;

    internal BookAutomationSnapshot GetBookAutomationSnapshot()
        => new(
            _bookState.AutomationState,
            _bookState.AutomationBookId,
            _bookState.AutomationBookName,
            _bookState.CurrentObjectiveKind,
            GetCurrentBookObjectiveLabel(),
            _bookState.AutomationStatus,
            _bookState.CompletedObjectives,
            _bookState.TotalObjectives);

    internal unsafe bool StartBookAutomation()
    {
        if (_bookState.AutomationState == BookAutomationState.Running)
            return false;

        StopFateGrindingInternal(true);

        if (!Service.Configuration.IsAtmaManagerEnabled)
        {
            ZodiacBuddyPlugin.PrintError("Enable Atma Manager before starting book automation.");
            return false;
        }

        if (!Svc.ClientState.IsLoggedIn || IsBookAutomationTransitionBlocked())
        {
            ZodiacBuddyPlugin.PrintError("Book automation can only be started while logged in and outside a duty/zone transition.");
            return false;
        }

        var relicNote = RelicNote.Instance();
        if (relicNote == null || relicNote->RelicNoteId == 0)
        {
            ZodiacBuddyPlugin.PrintError("No active Trials of the Braves book could be resolved.");
            return false;
        }

        var bookId = relicNote->RelicNoteId;
        if (!BraveBook.TryGetValue(bookId, out var book))
        {
            ZodiacBuddyPlugin.PrintError($"No ZodiacBuddy book data is available for RelicNoteId={bookId}.");
            return false;
        }

        if (_dungeonAutoDutyStarted && !AutoDutyIpc.IsStopped())
            AutoDutyIpc.Stop();
        CancelActiveRun();
        _bookState.AutomationBookId = bookId;
        _bookState.AutomationBookName = book.Name;
        _bookState.CurrentObjectiveKind = BookObjectiveKind.None;
        _bookState.CurrentObjective = null;
        _bookState.TotalObjectives = CountBookObjectives(book);
        _bookState.CompletedObjectives = CountCompletedBookObjectives(relicNote, book);
        _bookCoordinator.Reset(relicNote, book);

        if (_bookState.TotalObjectives > 0 && _bookState.CompletedObjectives >= _bookState.TotalObjectives)
        {
            _bookState.AutomationState = BookAutomationState.Completed;
            _bookState.AutomationStatus = $"{book.Name} is already complete.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] bookId={bookId} name='{book.Name}' is already complete ({_bookState.CompletedObjectives}/{_bookState.TotalObjectives}).");
            Service.Plugin.PrintStepProgress(_bookState.AutomationStatus);
            return true;
        }

        _bookState.AutomationState = BookAutomationState.Running;
        _bookState.AutomationStatus = $"Starting {book.Name} from {_bookState.CompletedObjectives}/{_bookState.TotalObjectives} completed objectives.";
        LogBookProgressSnapshot(relicNote, book, "start");
        LogBookSlotSnapshot(relicNote, book);
        Service.Plugin.PrintStepProgress($"Starting {book.Name} ({_bookState.CompletedObjectives}/{_bookState.TotalObjectives} complete).");
        Svc.Framework.Update -= TickBookAutomation;
        Svc.Framework.Update += TickBookAutomation;
        AdvanceBookAutomation();
        return _bookState.AutomationState is BookAutomationState.Running or BookAutomationState.Completed;
    }

    internal void CancelBookAutomation()
    {
        if (_bookState.AutomationState != BookAutomationState.Running)
            return;

        FinishBookAutomation(BookAutomationState.Cancelled, "Book automation stopped.", true);
    }

    private void CancelBookAutomationForManualObjective()
    {
        if (_bookState.AutomationState != BookAutomationState.Running)
            return;

        Svc.Framework.Update -= TickBookAutomation;
        StopBookFateGrinding("manual book objective selected", true);
        if (_bookState.CurrentObjectiveKind == BookObjectiveKind.Dungeon && _dungeonAutoDutyStarted && !AutoDutyIpc.IsStopped())
            AutoDutyIpc.Stop();
        CancelActiveRun();
        _bookState.AutomationState = BookAutomationState.Cancelled;
        _bookState.AutomationStatus = "Book automation stopped because a book objective was selected manually.";
        _bookState.CurrentObjectiveKind = BookObjectiveKind.None;
        _bookState.CurrentObjective = null;
        _bookState.CurrentFateProbe = false;
        _bookState.CurrentLeveReroll = false;
        Service.PluginLog.Verbose("[ZodiacBuddy/BOOK] Automatic book sequencing stopped because the user selected an objective manually.");
    }

    private void TickBookAutomation(IFramework framework)
        => AdvanceBookAutomation();

    private void ClearCurrentBookObjective()
    {
        _bookState.CurrentObjectiveKind = BookObjectiveKind.None;
        _bookState.CurrentObjective = null;
        _bookState.CurrentFateProbe = false;
        _bookState.CurrentLeveReroll = false;
    }

    private bool DispatchCurrentBookObjective()
    {
        return (_bookState.CurrentObjectiveKind, _bookState.CurrentObjective) switch
        {
            (BookObjectiveKind.Enemy, EnemyObjectiveDefinition target)
                => StartEnemyAutomation(target, _bookState.AutomationBookId),
            (BookObjectiveKind.Dungeon, DungeonObjectiveDefinition target)
                => StartDungeonAutomation(target, _bookState.AutomationBookId, true),
            (BookObjectiveKind.Fate, FateObjectiveDefinition target)
                => StartBookFateObjective(target),
            (BookObjectiveKind.Leve, LeveObjectiveDefinition target)
                => StartLeveAutomation(
                    target,
                    _bookState.CurrentLeveReroll ? 0 : _bookState.AutomationBookId,
                    false,
                    allowUnavailableReturn: true,
                    rerollMode: _bookState.CurrentLeveReroll),
            _ => false,
        };
    }

    private bool StartBookFateObjective(FateObjectiveDefinition target)
    {
        StartFateAutomation(target, _bookState.AutomationBookId, false, _bookState.CurrentFateProbe);
        return _fateAutomation.State != FateAutomationState.Failed;
    }

    private bool CanAdvancePastCurrentBookObjective()
    {
        return _bookState.CurrentObjectiveKind switch
        {
            BookObjectiveKind.Enemy => IsCompletedEnemyObjectiveReadyForPlannerRelease(),
            BookObjectiveKind.Dungeon => _dungeonAutomationState == DungeonAutomationState.Complete,
            BookObjectiveKind.Fate => _fateAutomation.State is FateAutomationState.Completed or FateAutomationState.Unavailable or FateAutomationState.RetryLater or FateAutomationState.Failed or FateAutomationState.Preempted or FateAutomationState.Cancelled,
            BookObjectiveKind.Leve => _leveAutomation.State is LeveAutomationState.Complete or LeveAutomationState.Unavailable or LeveAutomationState.Failed or LeveAutomationState.Idle,
            _ => true,
        };
    }

    private bool HasCurrentBookObjectiveFailed()
    {
        return _bookState.CurrentObjectiveKind switch
        {
            BookObjectiveKind.Enemy => _enemyAutomationFailed || !_automationRun.HasActiveRun,
            BookObjectiveKind.Dungeon => _dungeonAutomationState is DungeonAutomationState.Failed or DungeonAutomationState.Cancelled,
            BookObjectiveKind.Fate => _fateAutomation.State is FateAutomationState.Failed or FateAutomationState.Preempted or FateAutomationState.Cancelled,
            BookObjectiveKind.Leve => _leveAutomation.State is LeveAutomationState.Failed or LeveAutomationState.Idle,
            _ => true,
        };
    }

    private unsafe bool IsBookObjectiveComplete(RelicNote* relicNote, BookObjectiveKind kind, IBraveObjectiveDefinition target)
    {
        return (kind, target) switch
        {
            (BookObjectiveKind.Enemy, EnemyObjectiveDefinition enemy)
                => enemy.MonsterSlot is >= 0 and <= 9 && relicNote->GetMonsterProgress(enemy.MonsterSlot) >= 3,
            (BookObjectiveKind.Dungeon, DungeonObjectiveDefinition dungeon)
                => dungeon.DungeonSlot is >= 0 and <= 2 && relicNote->IsDungeonComplete(dungeon.DungeonSlot),
            (BookObjectiveKind.Fate, FateObjectiveDefinition fate)
                => fate.FateSlot is >= 0 and <= 2 && relicNote->IsFateComplete(fate.FateSlot),
            (BookObjectiveKind.Leve, LeveObjectiveDefinition leve)
                => leve.LeveSlot is >= 0 and <= 2 && relicNote->IsLeveComplete(leve.LeveSlot),
            _ => false,
        };
    }

    private bool IsBookAutomationTransitionBlocked()
        => Svc.Condition[ConditionFlag.BetweenAreas]
            || Svc.Condition[ConditionFlag.BetweenAreas51]
            || Svc.Condition[ConditionFlag.BoundByDuty];

    private void FailBookAutomation(string message)
        => FinishBookAutomation(BookAutomationState.Failed, message, true);

    private void FinishBookAutomation(BookAutomationState state, string status, bool cancelObjective)
    {
        Svc.Framework.Update -= TickBookAutomation;
        StopBookFateGrinding("book automation finished", cancelObjective);

        if (cancelObjective)
        {
            if (_bookState.CurrentObjectiveKind == BookObjectiveKind.Dungeon && _dungeonAutoDutyStarted && !AutoDutyIpc.IsStopped())
                AutoDutyIpc.Stop();
            CancelActiveRun();
        }

        _bookState.AutomationState = state;
        _bookState.AutomationStatus = status;
        ClearCurrentBookObjective();
        _bookState.FateTerritoryDwellUntil = DateTime.MinValue;

        if (state == BookAutomationState.Completed)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] {status} completed={_bookState.CompletedObjectives}/{_bookState.TotalObjectives}.");
            Service.Plugin.PrintStepProgress(status);
        }
        else if (state == BookAutomationState.Failed)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/BOOK] {status}");
            ZodiacBuddyPlugin.PrintError(status);
        }
        else if (state == BookAutomationState.Cancelled)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] {status}");
            Service.Plugin.PrintStepProgress(status);
        }
    }
}
