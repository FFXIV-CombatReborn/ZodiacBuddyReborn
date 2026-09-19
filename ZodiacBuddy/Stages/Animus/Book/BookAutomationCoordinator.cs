using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal readonly record struct BookWorkSelection(
    BookObjectiveKind Kind,
    IBraveObjectiveDefinition Target,
    bool FateProbe,
    bool LeveReroll);

internal sealed class BookAutomationCoordinator
{
    private readonly int enemyBatchSize;
    private readonly int fateTerritoryDwellSeconds;

    internal BookAutomationCoordinator(int enemyBatchSize, int fateTerritoryDwellSeconds)
    {
        this.enemyBatchSize = enemyBatchSize;
        this.fateTerritoryDwellSeconds = fateTerritoryDwellSeconds;
    }

    internal BookPlannerState State { get; } = new();

    internal unsafe void Reset(RelicNote* relicNote, BraveBook book)
    {
        State.FateProbedSlots.Clear();
        State.UnavailableLeveIds.Clear();
        State.UnavailableRerollLeveIds.Clear();
        State.FateSweepPending = HasIncompleteFates(relicNote, book);
        State.CurrentFateProbe = false;
        State.CurrentLeveReroll = false;
        State.EnemyBatchCompleted = 0;
        State.EnemyBatchTerritory = 0;
        State.LeveOfferGeneration = 1;
        State.LastBlockedLeveIssuer = string.Empty;
        State.FateTerritoryDwellUntil = DateTime.MinValue;
        State.PlannerBlockReason = string.Empty;
        State.FateGrindingActive = false;
        State.FateGrindingFillerId = 0;
        State.FateGrindingAttempts = 0;
        State.FateGrindingStatus = string.Empty;
        State.FateGrindingLastResult = ZodiacBuddy.Systems.Fates.FateExecutionResult.None;
        State.FateGrindingBackoffUntil.Clear();
        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Planner initialized fateSweepPending={State.FateSweepPending} enemyBatchSize={enemyBatchSize} fateTerritoryDwell={fateTerritoryDwellSeconds}s leveOfferGeneration={State.LeveOfferGeneration}.");
    }

    internal unsafe void HandleCompletedWork(RelicNote* relicNote, BraveBook book, BookObjectiveKind kind, IBraveObjectiveDefinition target)
    {
        switch (kind)
        {
            case BookObjectiveKind.Fate:
                RequestFateSweepIfNeeded(relicNote, book, "required FATE completed");
                break;
            case BookObjectiveKind.Dungeon:
                RequestFateSweepIfNeeded(relicNote, book, "dungeon work unit completed");
                break;
            case BookObjectiveKind.Enemy:
                if (HasIncompleteFates(relicNote, book))
                {
                    State.EnemyBatchCompleted++;
                    if (State.EnemyBatchTerritory == 0)
                        State.EnemyBatchTerritory = target.Position.TerritoryType.RowId;
                    if (State.EnemyBatchCompleted >= enemyBatchSize)
                        RequestFateSweep(relicNote, book, $"enemy batch reached {enemyBatchSize} objectives");
                }
                break;
            case BookObjectiveKind.Leve:
                InvalidateLeveOfferGeneration("book leve completed");
                RequestFateSweepIfNeeded(relicNote, book, "leve work unit completed");
                break;
        }
    }

    internal void InvalidateLeveOfferGeneration(string reason)
    {
        State.LeveOfferGeneration++;
        State.UnavailableLeveIds.Clear();
        State.UnavailableRerollLeveIds.Clear();
        State.LastBlockedLeveIssuer = string.Empty;
        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Leve offerings invalidated after {reason}; offerGeneration={State.LeveOfferGeneration}.");
    }

    internal unsafe void RequestFateSweepIfNeeded(RelicNote* relicNote, BraveBook book, string reason)
    {
        if (HasIncompleteFates(relicNote, book))
            RequestFateSweep(relicNote, book, reason);
    }

    internal unsafe void RequestFateSweep(RelicNote* relicNote, BraveBook book, string reason)
    {
        if (!HasIncompleteFates(relicNote, book))
        {
            State.FateSweepPending = false;
            State.FateProbedSlots.Clear();
            State.FateTerritoryDwellUntil = DateTime.MinValue;
            return;
        }

        State.FateSweepPending = true;
        State.FateProbedSlots.Clear();
        State.FateTerritoryDwellUntil = DateTime.MinValue;
        State.EnemyBatchCompleted = 0;
        State.EnemyBatchTerritory = 0;
        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] FATE sweep requested reason='{reason}'.");
    }

    internal void BeginFateTerritoryDwellWait()
    {
        if (State.FateTerritoryDwellUntil != DateTime.MinValue)
            return;

        State.FateTerritoryDwellUntil = DateTime.Now.AddSeconds(fateTerritoryDwellSeconds);
        State.AutomationStatus = "No deterministic book work remains; waiting for required FATEs before another zone sweep.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] No deterministic work remains. Waiting {fateTerritoryDwellSeconds}s in the current territory before sweeping remaining FATE zones again.");
        Service.Plugin.PrintStepProgress($"No required FATE is active. Waiting {fateTerritoryDwellSeconds}s before checking the remaining FATE zones again.");
    }

    internal unsafe bool TrySelectNextWork(RelicNote* relicNote, BraveBook book, out BookWorkSelection selection)
    {
        State.PlannerBlockReason = string.Empty;
        var hasIncompleteFates = HasIncompleteFates(relicNote, book);

        if (hasIncompleteFates && State.FateSweepPending)
        {
            if (TryGetNextFateProbe(relicNote, book, out var fateTarget))
            {
                selection = new(BookObjectiveKind.Fate, fateTarget, true, false);
                return true;
            }

            CompleteFateSweep();
        }

        foreach (var candidate in book.Dungeons)
        {
            if (candidate.DungeonSlot is >= 0 and <= 2 && !relicNote->IsDungeonComplete(candidate.DungeonSlot))
            {
                selection = new(BookObjectiveKind.Dungeon, candidate, false, false);
                return true;
            }
        }

        var incompleteEnemies = new List<EnemyObjectiveDefinition>();
        foreach (var candidate in book.Enemies)
        {
            if (candidate.MonsterSlot is >= 0 and <= 9 && relicNote->GetMonsterProgress(candidate.MonsterSlot) < 3)
                incompleteEnemies.Add(candidate);
        }

        if (incompleteEnemies.Count > 0)
        {
            if (hasIncompleteFates && State.EnemyBatchCompleted >= enemyBatchSize)
            {
                RequestFateSweep(relicNote, book, "enemy work-unit cadence");
                return TrySelectNextWork(relicNote, book, out selection);
            }

            var enemyTarget = SelectNextEnemy(incompleteEnemies);
            if (State.EnemyBatchCompleted == 0 || State.EnemyBatchTerritory == 0)
                State.EnemyBatchTerritory = enemyTarget.Position.TerritoryType.RowId;
            selection = new(BookObjectiveKind.Enemy, enemyTarget, false, false);
            return true;
        }

        if (hasIncompleteFates && State.EnemyBatchCompleted > 0)
        {
            RequestFateSweep(relicNote, book, "enemy objectives exhausted before the normal batch size");
            return TrySelectNextWork(relicNote, book, out selection);
        }

        var incompleteBookLeves = new List<LeveObjectiveDefinition>();
        foreach (var candidate in book.Leves)
        {
            if (candidate.LeveId != 0 && candidate.LeveSlot is >= 0 and <= 2 && !relicNote->IsLeveComplete(candidate.LeveSlot))
                incompleteBookLeves.Add(candidate);
        }

        var availableBookLeves = incompleteBookLeves
            .Where(candidate => !State.UnavailableLeveIds.Contains(candidate.LeveId))
            .OrderByDescending(candidate => !string.IsNullOrEmpty(State.LastBlockedLeveIssuer) && candidate.Issuer == State.LastBlockedLeveIssuer)
            .ThenByDescending(candidate => candidate.ZoneId == Service.ClientState.TerritoryType)
            .ToArray();

        if (availableBookLeves.Length > 0)
        {
            selection = new(BookObjectiveKind.Leve, availableBookLeves[0], false, false);
            return true;
        }

        if (incompleteBookLeves.Count > 0)
        {
            var currentBookLeveIds = book.Leves.Select(candidate => candidate.LeveId).ToHashSet();
            var rerollCandidates = BraveBook.GetAllLeveTargets()
                .Where(candidate => candidate.LeveId != 0
                    && !currentBookLeveIds.Contains(candidate.LeveId)
                    && !State.UnavailableRerollLeveIds.Contains(candidate.LeveId)
                    && LeveExecutionProfiles.TryGet(candidate.LeveId, out _))
                .OrderByDescending(candidate => !string.IsNullOrEmpty(State.LastBlockedLeveIssuer) && candidate.Issuer == State.LastBlockedLeveIssuer)
                .ThenByDescending(candidate => candidate.ZoneId == Service.ClientState.TerritoryType)
                .ThenBy(candidate => candidate.ZoneName)
                .ThenBy(candidate => candidate.Issuer)
                .ThenBy(candidate => candidate.Name)
                .ToArray();

            if (rerollCandidates.Length > 0)
            {
                var target = rerollCandidates[0];
                selection = new(BookObjectiveKind.Leve, target, false, true);
                Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] All remaining book leves are unavailable in offerGeneration={State.LeveOfferGeneration}; selecting known supported reroll LeveId={target.LeveId} name='{target.Name}' issuer='{target.Issuer}'.");
                return true;
            }

            State.PlannerBlockReason = $"All remaining book leves are unavailable in offer generation {State.LeveOfferGeneration}, and no untried known supported leve remains to force a reroll.";
        }

        selection = default;
        return false;
    }

    internal unsafe bool IsFateOnlyRemainder(RelicNote* relicNote, BraveBook book)
    {
        foreach (var candidate in book.Enemies)
        {
            if (candidate.MonsterSlot is >= 0 and <= 9 && relicNote->GetMonsterProgress(candidate.MonsterSlot) < 3)
                return false;
        }

        foreach (var candidate in book.Dungeons)
        {
            if (candidate.DungeonSlot is >= 0 and <= 2 && !relicNote->IsDungeonComplete(candidate.DungeonSlot))
                return false;
        }

        foreach (var candidate in book.Leves)
        {
            if (candidate.LeveId != 0 && candidate.LeveSlot is >= 0 and <= 2 && !relicNote->IsLeveComplete(candidate.LeveSlot))
                return false;
        }

        return HasIncompleteFates(relicNote, book);
    }

    internal unsafe bool HasIncompleteFates(RelicNote* relicNote, BraveBook book)
    {
        foreach (var candidate in book.Fates)
        {
            if (candidate.FateId != 0
                && candidate.FateSlot is >= 0 and <= 2
                && !relicNote->IsFateComplete(candidate.FateSlot))
                return true;
        }

        return false;
    }

    internal unsafe bool TryGetActionableIncompleteFateInCurrentTerritory(
        RelicNote* relicNote,
        BraveBook book,
        Func<FateObjectiveDefinition, bool> isActionable,
        out FateObjectiveDefinition target)
    {
        foreach (var candidate in book.Fates)
        {
            if (candidate.FateId == 0
                || candidate.FateSlot is < 0 or > 2
                || relicNote->IsFateComplete(candidate.FateSlot)
                || !isActionable(candidate))
                continue;

            target = candidate;
            return true;
        }

        target = default;
        return false;
    }

    private void CompleteFateSweep()
    {
        State.FateSweepPending = false;
        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] FATE sweep completed with no currently actionable required FATEs; checkedSlots=[{string.Join(",", State.FateProbedSlots.OrderBy(slot => slot))}].");
    }

    private unsafe bool TryGetNextFateProbe(RelicNote* relicNote, BraveBook book, out FateObjectiveDefinition target)
    {
        FateObjectiveDefinition? fallback = null;
        foreach (var candidate in book.Fates)
        {
            if (candidate.FateId == 0
                || candidate.FateSlot is < 0 or > 2
                || relicNote->IsFateComplete(candidate.FateSlot)
                || State.FateProbedSlots.Contains(candidate.FateSlot))
                continue;

            if (candidate.Position.TerritoryType.RowId == Service.ClientState.TerritoryType)
            {
                target = candidate;
                return true;
            }

            fallback ??= candidate;
        }

        if (fallback.HasValue)
        {
            target = fallback.Value;
            return true;
        }

        target = default;
        return false;
    }

    private EnemyObjectiveDefinition SelectNextEnemy(IReadOnlyList<EnemyObjectiveDefinition> candidates)
    {
        if (State.EnemyBatchTerritory != 0)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Position.TerritoryType.RowId == State.EnemyBatchTerritory)
                    return candidate;
            }
        }

        var currentTerritory = Service.ClientState.TerritoryType;
        var localCandidates = candidates.Where(candidate => candidate.Position.TerritoryType.RowId == currentTerritory).ToArray();
        if (localCandidates.Length > 0)
            return localCandidates[0];

        var grouped = candidates
            .GroupBy(candidate => candidate.Position.TerritoryType.RowId)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First();

        return grouped.First();
    }
}
