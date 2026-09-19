using System;
using System.Collections.Generic;
using System.Linq;

using Lumina.Excel.Sheets;

namespace ZodiacBuddy.Stages.Animus.Data;

internal struct BraveBook {
    private static readonly Dictionary<uint, BraveBook> Dataset = [];

    static BraveBook() {
        PopulateDataset();
    }

    public string Name { get; init; }

    public EnemyObjectiveDefinition[] Enemies { get; init; }

    public DungeonObjectiveDefinition[] Dungeons { get; init; }

    public FateObjectiveDefinition[] Fates { get; init; }

    public LeveObjectiveDefinition[] Leves { get; init; }

    public static BraveBook GetValue(uint bookId)
        => Dataset[bookId];

    internal static bool TryGetValue(uint bookId, out BraveBook book)
        => Dataset.TryGetValue(bookId, out book);

    internal static IReadOnlyList<FateObjectiveDefinition> GetAllFateTargets()
        => Dataset.Values
            .SelectMany(book => book.Fates)
            .Where(target => target.FateId != 0)
            .GroupBy(target => target.FateId)
            .Select(group => group.First())
            .OrderBy(target => target.ZoneName)
            .ThenBy(target => target.Name)
            .ToArray();

    internal static IReadOnlyList<LeveObjectiveDefinition> GetAllLeveTargets()
        => Dataset.Values
            .SelectMany(book => book.Leves)
            .Where(target => target.LeveId != 0)
            .GroupBy(target => target.LeveId)
            .Select(group => group.First())
            .OrderBy(target => target.ZoneName)
            .ThenBy(target => target.Issuer)
            .ThenBy(target => target.Name)
            .ToArray();

    internal static bool TryGetLeveTarget(uint bookId, uint leveId, out LeveObjectiveDefinition target)
    {
        if (Dataset.TryGetValue(bookId, out var book))
        {
            foreach (var candidate in book.Leves)
            {
                if (candidate.LeveId == leveId)
                {
                    target = candidate;
                    return true;
                }
            }
        }

        target = default;
        return false;
    }

    private static void PopulateDataset() {
        try {
            var relicNoteSheet = Service.DataManager.GetExcelSheet<RelicNote>();

            foreach (var bookRow in relicNoteSheet) {
                if (!bookRow.EventItem.IsValid)
                    continue;
                var eventItem = bookRow.EventItem.Value;

                var bookName = eventItem.Name.ExtractText();

                var enemyCount = bookRow.MonsterNoteTargetCommon.Count;
                var dungeonCount = bookRow.MonsterNoteTargetNM.Count;
                var fateCount = bookRow.Fate.Count;
                var leveCount = bookRow.Leve.Count;

                var braveBook = Dataset[bookRow.RowId] = new BraveBook {
                    Name = bookName,
                    Enemies = new EnemyObjectiveDefinition[enemyCount],
                    Dungeons = new DungeonObjectiveDefinition[dungeonCount],
                    Fates = new FateObjectiveDefinition[fateCount],
                    Leves = new LeveObjectiveDefinition[leveCount],
                };

                for (var i = 0; i < enemyCount; i++) {
                    var mntc = bookRow.MonsterNoteTargetCommon[i].Value;

                    var zoneRow = mntc.PlaceNameZone[0].Value;
                    var zoneName = zoneRow.Name.ExtractText();
                    var zoneId = zoneRow.RowId;

                    var locationName = mntc.PlaceNameLocation[0].Value.Name.ExtractText();

                    var name = mntc.BNpcName.Value.Singular.ExtractText();

                    var position = BraveBookLocationCatalog.GetMonsterPosition(mntc.RowId);

                    braveBook.Enemies[i] = new EnemyObjectiveDefinition {
                        Name = name,
                        MonsterNoteTargetId = mntc.RowId,
                        MonsterSlot = i,
                        BNpcNameId = mntc.BNpcName.RowId,
                        ZoneName = zoneName,
                        ZoneId = zoneId,
                        LocationName = locationName,
                        Position = position,
                    };
                }

                for (var i = 0; i < dungeonCount; i++) {
                    var mntc = bookRow.MonsterNoteTargetNM[i].Value;

                    var zoneRow = mntc.PlaceNameZone[0].Value;
                    var zoneName = zoneRow.Name.ExtractText();
                    var zoneId = zoneRow.RowId;

                    var locationName = mntc.PlaceNameLocation[0].Value.Name.ExtractText();

                    var name = mntc.BNpcName.Value.Singular;

                    var position = BraveBookLocationCatalog.GetMonsterPosition(mntc.RowId);

                    var cfcId = position.TerritoryType.Value.ContentFinderCondition.Value.RowId;

                    braveBook.Dungeons[i] = new DungeonObjectiveDefinition {
                        Name = name.ExtractText(),
                        ZoneName = zoneName,
                        ZoneId = zoneId,
                        LocationName = locationName,
                        Position = position,
                        ContentsFinderConditionId = cfcId,
                        DungeonSlot = i,
                    };
                }

                for (var i = 0; i < fateCount; i++) {
                    var fate = bookRow.Fate[i].Value;
                    var fateId = fate.RowId;

                    var position = BraveBookLocationCatalog.GetFatePosition(fateId);

                    var zoneName = position.TerritoryType.Value.PlaceName.Value.Name.ExtractText();
                    var zoneId = position.TerritoryType.RowId;

                    var name = fate.Name;

                    braveBook.Fates[i] = new FateObjectiveDefinition {
                        Name = name.ExtractText(),
                        ZoneName = zoneName,
                        ZoneId = zoneId,
                        LocationName = string.Empty,
                        Position = position,
                        FateId = fateId,
                        FateSlot = i,
                    };
                }

                for (var i = 0; i < leveCount; i++) {
                    var leve = bookRow.Leve[i].Value;
                    var leveId = leve.RowId;
                    var leveName = leve.Name.ExtractText();

                    var position = BraveBookLocationCatalog.GetLevePosition(leveId);
                    var issuerName = BraveLeveCatalog.GetIssuer(leveId);

                    var zoneName = position.TerritoryType.Value.PlaceName.Value.Name.ExtractText();
                    var zoneId = position.TerritoryType.RowId;

                    braveBook.Leves[i] = new LeveObjectiveDefinition {
                        Name = leveName,
                        Issuer = issuerName,
                        ZoneName = zoneName,
                        ZoneId = zoneId,
                        LocationName = string.Empty,
                        Position = position,
                        LeveId = leveId,
                        LeveSlot = i,
                    };
                }
            }
        }
        catch (Exception ex) {
            Service.PluginLog.Error(ex, "[ZodiacBuddy/BOOK] An error occurred during plugin data load.");
            throw;
        }
    }
}
