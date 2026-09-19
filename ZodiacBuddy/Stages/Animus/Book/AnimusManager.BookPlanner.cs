using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe void AdvanceBookAutomation()
    {
        if (_bookState.AutomationState != BookAutomationState.Running)
            return;

        if (!Service.Configuration.IsAtmaManagerEnabled)
        {
            FailBookAutomation("Atma Manager was disabled while book automation was running.");
            return;
        }

        if (!Svc.ClientState.IsLoggedIn || IsBookAutomationTransitionBlocked())
            return;

        var relicNote = RelicNote.Instance();
        if (relicNote == null)
            return;

        if (relicNote->RelicNoteId != _bookState.AutomationBookId)
        {
            if (_bookState.CurrentObjectiveKind == BookObjectiveKind.Dungeon && IsDungeonEnvironmentActive())
                return;

            FailBookAutomation("The active Trials of the Braves book changed while book automation was running.");
            return;
        }

        if (!BraveBook.TryGetValue(_bookState.AutomationBookId, out var book))
        {
            FailBookAutomation($"Book data disappeared for RelicNoteId={_bookState.AutomationBookId}.");
            return;
        }

        _bookState.TotalObjectives = CountBookObjectives(book);
        _bookState.CompletedObjectives = CountCompletedBookObjectives(relicNote, book);

        if (HandleCurrentBookObjective(relicNote, book))
            return;

        if (HandleBookFateGrinding(relicNote, book))
            return;

        if (HandleFateTerritoryDwellWait(relicNote, book))
            return;

        if (!_bookCoordinator.TrySelectNextWork(relicNote, book, out var selection))
        {
            _bookState.CompletedObjectives = CountCompletedBookObjectives(relicNote, book);
            if (_bookState.TotalObjectives > 0 && _bookState.CompletedObjectives >= _bookState.TotalObjectives)
            {
                FinishBookAutomation(BookAutomationState.Completed, $"{book.Name} is complete.", true);
                return;
            }

            if (_bookCoordinator.HasIncompleteFates(relicNote, book))
            {
                if (_bookCoordinator.IsFateOnlyRemainder(relicNote, book))
                {
                    if (TryBeginBookFateGrinding(relicNote, book))
                        return;

                    _bookCoordinator.BeginFateTerritoryDwellWait();
                    return;
                }

                FailBookAutomation(string.IsNullOrEmpty(_bookState.PlannerBlockReason)
                    ? "No runnable non-FATE objective was found, so FATE grinding was not allowed to start."
                    : _bookState.PlannerBlockReason);
                return;
            }

            FailBookAutomation(string.IsNullOrEmpty(_bookState.PlannerBlockReason)
                ? "No runnable objective was found even though the current book is not complete."
                : _bookState.PlannerBlockReason);
            return;
        }

        var kind = selection.Kind;
        var target = selection.Target;
        var fateProbe = selection.FateProbe;
        var leveReroll = selection.LeveReroll;
        _bookState.CurrentObjectiveKind = kind;
        _bookState.CurrentObjective = target;
        _bookState.CurrentFateProbe = fateProbe;
        _bookState.CurrentLeveReroll = leveReroll;
        _bookState.AutomationStatus = $"Starting {GetCurrentBookObjectiveLabel()} ({_bookState.CompletedObjectives}/{_bookState.TotalObjectives} complete).";
        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Dispatching {GetCurrentBookObjectiveLabel()} {GetBookObjectiveDebugIdentity(kind, target)} fateProbe={fateProbe} leveReroll={leveReroll} progress={_bookState.CompletedObjectives}/{_bookState.TotalObjectives}.");
        Service.Plugin.PrintStepProgress(leveReroll
            ? $"Next: Leve reroll - {target.Name}."
            : $"Next: {kind} - {target.Name}.");

        if (!DispatchCurrentBookObjective())
            FailBookAutomation($"Could not start {GetCurrentBookObjectiveLabel()}.");
    }
    private unsafe bool HandleCurrentBookObjective(RelicNote* relicNote, BraveBook book)
    {
        if (_bookState.CurrentObjectiveKind == BookObjectiveKind.None)
            return false;

        var currentObjective = _bookState.CurrentObjective;
        if (currentObjective is null)
        {
            FailBookAutomation("The book planner lost its current objective definition.");
            return true;
        }

        if (_bookState.CurrentLeveReroll)
        {
            if (currentObjective is not LeveObjectiveDefinition rerollTarget)
            {
                FailBookAutomation("The book planner's leve reroll objective has the wrong definition type.");
                return true;
            }

            if (_leveAutomation.State == LeveAutomationState.Complete)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Reroll leve completed: leveId={rerollTarget.LeveId} name='{rerollTarget.Name}'.");
                _bookCoordinator.InvalidateLeveOfferGeneration("reroll leve completed");
                _bookCoordinator.RequestFateSweepIfNeeded(relicNote, book, "leve reroll completed");
                ClearCurrentBookObjective();
                return false;
            }

            if (_leveAutomation.State == LeveAutomationState.Unavailable)
            {
                _bookState.UnavailableRerollLeveIds.Add(rerollTarget.LeveId);
                _bookState.LastBlockedLeveIssuer = rerollTarget.Issuer ?? string.Empty;
                Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Reroll LeveId={rerollTarget.LeveId} unavailable in offerGeneration={_bookState.LeveOfferGeneration}; trying another known supported leve.");
                ClearCurrentBookObjective();
                return false;
            }

            if (_leveAutomation.State is LeveAutomationState.Failed or LeveAutomationState.Idle)
            {
                FailBookAutomation($"Leve reroll {rerollTarget.Name} stopped before completion.");
                return true;
            }

            UpdateCurrentBookObjectiveStatus(relicNote);
            return true;
        }

        if (_bookState.CurrentObjectiveKind == BookObjectiveKind.Fate)
        {
            if (currentObjective is not FateObjectiveDefinition fateTarget)
            {
                FailBookAutomation("The book planner's FATE objective has the wrong definition type.");
                return true;
            }

            if (_fateAutomation.State == FateAutomationState.Unavailable)
            {
                _bookState.FateProbedSlots.Add(fateTarget.FateSlot);
                Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] FATE probe unavailable: fateSlot={fateTarget.FateSlot} fateId={fateTarget.FateId} zone='{fateTarget.ZoneName}'.");
                ClearCurrentBookObjective();
                return false;
            }

            if (_fateAutomation.State == FateAutomationState.RetryLater
                && !IsBookObjectiveComplete(relicNote, _bookState.CurrentObjectiveKind, fateTarget))
            {
                _bookState.FateProbedSlots.Add(fateTarget.FateSlot);
                Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Required FATE attempt ended without book credit: fateSlot={fateTarget.FateSlot} fateId={fateTarget.FateId} name='{fateTarget.Name}'. Marking it checked for the current sweep and continuing book automation.");
                Service.Plugin.PrintStepProgress($"FATE {fateTarget.Name} failed. Continuing the book and retrying it later.");
                ClearCurrentBookObjective();
                return false;
            }
        }

        if (_bookState.CurrentObjectiveKind == BookObjectiveKind.Leve)
        {
            if (currentObjective is not LeveObjectiveDefinition leveTarget)
            {
                FailBookAutomation("The book planner's leve objective has the wrong definition type.");
                return true;
            }

            if (_leveAutomation.State == LeveAutomationState.Unavailable)
            {
                _bookState.UnavailableLeveIds.Add(leveTarget.LeveId);
                _bookState.LastBlockedLeveIssuer = leveTarget.Issuer ?? string.Empty;
                Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Book LeveId={leveTarget.LeveId} unavailable in offerGeneration={_bookState.LeveOfferGeneration}; retaining it as incomplete and trying another leve.");
                ClearCurrentBookObjective();
                return false;
            }
        }

        if (IsBookObjectiveComplete(relicNote, _bookState.CurrentObjectiveKind, currentObjective))
        {
            if (_bookState.CurrentObjectiveKind == BookObjectiveKind.Enemy)
                PrepareCompletedEnemyObjectiveForPlannerRelease();

            if (!CanAdvancePastCurrentBookObjective())
            {
                _bookState.AutomationStatus = $"Book credit confirmed for {GetCurrentBookObjectiveLabel()}; waiting for subsystem cleanup.";
                return true;
            }

            var completedKind = _bookState.CurrentObjectiveKind;
            var completedTarget = currentObjective;
            var completedLabel = GetCurrentBookObjectiveLabel();
            _bookState.AutomationStatus = $"Completed {completedLabel}; selecting the next work unit.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Objective completed: {completedLabel}; authoritative book progress={_bookState.CompletedObjectives}/{_bookState.TotalObjectives}.");
            LogBookProgressSnapshot(relicNote, book, "objective-complete");
            _bookCoordinator.HandleCompletedWork(relicNote, book, completedKind, completedTarget);
            ClearCurrentBookObjective();
            return false;
        }

        if (HasCurrentBookObjectiveFailed())
        {
            FailBookAutomation($"{GetCurrentBookObjectiveLabel()} stopped before its book objective completed.");
            return true;
        }

        UpdateCurrentBookObjectiveStatus(relicNote);
        return true;
    }

    private unsafe bool HandleFateTerritoryDwellWait(RelicNote* relicNote, BraveBook book)
    {
        if (_bookState.FateTerritoryDwellUntil == DateTime.MinValue)
            return false;

        if (!_bookCoordinator.HasIncompleteFates(relicNote, book))
        {
            _bookState.FateTerritoryDwellUntil = DateTime.MinValue;
            return false;
        }

        if (_bookCoordinator.TryGetActionableIncompleteFateInCurrentTerritory(relicNote, book, IsBookFateActionableInCurrentTerritory, out var localFate))
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Required FATE became actionable while idling: fateId={localFate.FateId} name='{localFate.Name}'.");
            _bookCoordinator.RequestFateSweep(relicNote, book, "required FATE became actionable in the current territory");
            return false;
        }

        if (DateTime.Now < _bookState.FateTerritoryDwellUntil)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((_bookState.FateTerritoryDwellUntil - DateTime.Now).TotalSeconds));
            _bookState.AutomationStatus = $"Waiting for remaining FATEs; next zone sweep in {seconds}s.";
            return true;
        }

        _bookCoordinator.RequestFateSweep(relicNote, book, "idle FATE wait elapsed");
        return false;
    }

    private unsafe void UpdateCurrentBookObjectiveStatus(RelicNote* relicNote)
    {
        _bookState.AutomationStatus = (_bookState.CurrentObjectiveKind, _bookState.CurrentObjective) switch
        {
            (BookObjectiveKind.Enemy, EnemyObjectiveDefinition enemy)
                => $"Working on {GetCurrentBookObjectiveLabel()}: {relicNote->GetMonsterProgress(enemy.MonsterSlot)}/3.",
            (BookObjectiveKind.Dungeon, DungeonObjectiveDefinition) => _dungeonAutomationStatus,
            (BookObjectiveKind.Fate, FateObjectiveDefinition) => _fateAutomation.Status,
            (BookObjectiveKind.Leve, LeveObjectiveDefinition) => _leveAutomation.Status,
            _ => _bookState.AutomationStatus,
        };
    }
}
