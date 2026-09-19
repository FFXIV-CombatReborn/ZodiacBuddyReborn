using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ZodiacBuddy.Stages.Animus.Data;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe int CountCompletedBookObjectives(RelicNote* relicNote, BraveBook book)
    {
        var completed = 0;

        foreach (var target in book.Enemies)
        {
            if (target.MonsterSlot is >= 0 and <= 9 && relicNote->GetMonsterProgress(target.MonsterSlot) >= 3)
                completed++;
        }

        foreach (var target in book.Dungeons)
        {
            if (target.DungeonSlot is >= 0 and <= 2 && relicNote->IsDungeonComplete(target.DungeonSlot))
                completed++;
        }

        foreach (var target in book.Fates)
        {
            if (target.FateId != 0 && target.FateSlot is >= 0 and <= 2 && relicNote->IsFateComplete(target.FateSlot))
                completed++;
        }

        foreach (var target in book.Leves)
        {
            if (target.LeveId != 0 && target.LeveSlot is >= 0 and <= 2 && relicNote->IsLeveComplete(target.LeveSlot))
                completed++;
        }

        return completed;
    }
    private static int CountBookObjectives(BraveBook book)
        => book.Enemies.Length + book.Dungeons.Length + book.Fates.Length + book.Leves.Length;
    private unsafe void LogBookProgressSnapshot(RelicNote* relicNote, BraveBook book, string reason)
    {
        var enemyComplete = 0;
        var dungeonComplete = 0;
        var fateComplete = 0;
        var leveComplete = 0;

        foreach (var target in book.Enemies)
            if (target.MonsterSlot is >= 0 and <= 9 && relicNote->GetMonsterProgress(target.MonsterSlot) >= 3)
                enemyComplete++;
        foreach (var target in book.Dungeons)
            if (target.DungeonSlot is >= 0 and <= 2 && relicNote->IsDungeonComplete(target.DungeonSlot))
                dungeonComplete++;
        foreach (var target in book.Fates)
            if (target.FateSlot is >= 0 and <= 2 && relicNote->IsFateComplete(target.FateSlot))
                fateComplete++;
        foreach (var target in book.Leves)
            if (target.LeveSlot is >= 0 and <= 2 && relicNote->IsLeveComplete(target.LeveSlot))
                leveComplete++;

        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Scan reason={reason} bookId={_bookState.AutomationBookId} name='{book.Name}' complete={enemyComplete + dungeonComplete + fateComplete + leveComplete}/{CountBookObjectives(book)} enemies={enemyComplete}/{book.Enemies.Length} dungeons={dungeonComplete}/{book.Dungeons.Length} fates={fateComplete}/{book.Fates.Length} leves={leveComplete}/{book.Leves.Length}.");
    }
    private unsafe void LogBookSlotSnapshot(RelicNote* relicNote, BraveBook book)
    {
        var enemyState = new string[book.Enemies.Length];
        var dungeonState = new string[book.Dungeons.Length];
        var fateState = new string[book.Fates.Length];
        var leveState = new string[book.Leves.Length];

        for (var i = 0; i < book.Enemies.Length; i++)
        {
            var target = book.Enemies[i];
            var progress = target.MonsterSlot is >= 0 and <= 9 ? relicNote->GetMonsterProgress(target.MonsterSlot) : 0;
            enemyState[i] = $"{target.MonsterSlot}:{progress}/3@{target.MonsterNoteTargetId}";
        }
        for (var i = 0; i < book.Dungeons.Length; i++)
        {
            var target = book.Dungeons[i];
            var complete = target.DungeonSlot is >= 0 and <= 2 && relicNote->IsDungeonComplete(target.DungeonSlot);
            dungeonState[i] = $"{target.DungeonSlot}:{(complete ? "done" : "open")}@{target.ContentsFinderConditionId}";
        }
        for (var i = 0; i < book.Fates.Length; i++)
        {
            var target = book.Fates[i];
            var complete = target.FateSlot is >= 0 and <= 2 && relicNote->IsFateComplete(target.FateSlot);
            fateState[i] = $"{target.FateSlot}:{(complete ? "done" : "open")}@{target.FateId}";
        }
        for (var i = 0; i < book.Leves.Length; i++)
        {
            var target = book.Leves[i];
            var complete = target.LeveSlot is >= 0 and <= 2 && relicNote->IsLeveComplete(target.LeveSlot);
            leveState[i] = $"{target.LeveSlot}:{(complete ? "done" : "open")}@{target.LeveId}";
        }

        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Slots enemies=[{string.Join(",", enemyState)}] dungeons=[{string.Join(",", dungeonState)}] fates=[{string.Join(",", fateState)}] leves=[{string.Join(",", leveState)}].");
    }
    private static string GetBookObjectiveDebugIdentity(BookObjectiveKind kind, IBraveObjectiveDefinition target)
        => (kind, target) switch
        {
            (BookObjectiveKind.Enemy, EnemyObjectiveDefinition enemy)
                => $"monsterSlot={enemy.MonsterSlot} monsterNoteTargetId={enemy.MonsterNoteTargetId}",
            (BookObjectiveKind.Dungeon, DungeonObjectiveDefinition dungeon)
                => $"dungeonSlot={dungeon.DungeonSlot} cfc={dungeon.ContentsFinderConditionId} territory={dungeon.Position.TerritoryType.RowId}",
            (BookObjectiveKind.Fate, FateObjectiveDefinition fate)
                => $"fateSlot={fate.FateSlot} fateId={fate.FateId} territory={fate.Position.TerritoryType.RowId}",
            (BookObjectiveKind.Leve, LeveObjectiveDefinition leve)
                => $"leveSlot={leve.LeveSlot} leveId={leve.LeveId} territory={leve.ZoneId}",
            _ => string.Empty,
        };
    private string GetCurrentBookObjectiveLabel()
    {
        if (_bookState.CurrentObjectiveKind == BookObjectiveKind.None)
            return string.Empty;

        if (_bookState.CurrentLeveReroll)
            return $"Leve reroll: {_bookState.CurrentObjective?.Name ?? string.Empty}";

        return $"{_bookState.CurrentObjectiveKind}: {_bookState.CurrentObjective?.Name ?? string.Empty}";
    }
}
