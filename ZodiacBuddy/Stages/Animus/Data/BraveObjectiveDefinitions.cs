using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace ZodiacBuddy.Stages.Animus.Data;

internal interface IBraveObjectiveDefinition
{
    string Name { get; }
    string ZoneName { get; }
    uint ZoneId { get; }
    string LocationName { get; }
    MapLinkPayload Position { get; }
}

internal struct EnemyObjectiveDefinition : IBraveObjectiveDefinition
{
    public string Name { get; init; }
    public string ZoneName { get; init; }
    public uint ZoneId { get; init; }
    public string LocationName { get; init; }
    public MapLinkPayload Position { get; init; }
    public uint MonsterNoteTargetId { get; init; }
    public int MonsterSlot { get; init; }
    public uint BNpcNameId { get; init; }
}

internal struct DungeonObjectiveDefinition : IBraveObjectiveDefinition
{
    public string Name { get; init; }
    public string ZoneName { get; init; }
    public uint ZoneId { get; init; }
    public string LocationName { get; init; }
    public MapLinkPayload Position { get; init; }
    public uint ContentsFinderConditionId { get; init; }
    public int DungeonSlot { get; init; }
}

internal struct FateObjectiveDefinition : IBraveObjectiveDefinition
{
    public string Name { get; init; }
    public string ZoneName { get; init; }
    public uint ZoneId { get; init; }
    public string LocationName { get; init; }
    public MapLinkPayload Position { get; init; }
    public uint FateId { get; init; }
    public int FateSlot { get; init; }
}

internal struct LeveObjectiveDefinition : IBraveObjectiveDefinition
{
    public string Name { get; init; }
    public string Issuer { get; init; }
    public string ZoneName { get; init; }
    public uint ZoneId { get; init; }
    public string LocationName { get; init; }
    public MapLinkPayload Position { get; init; }
    public uint LeveId { get; init; }
    public int LeveSlot { get; init; }
}
