using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const byte FateRuleCollect = 2;
    private const byte FateRuleEscort = 3;
    private const byte FateRuleDefend = 4;

    private unsafe byte GetFateRule(ushort fateId)
    {
        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        return context == null ? (byte)0 : context->Rule;
    }

    private bool IsCollectFate(ushort fateId)
        => GetFateRule(fateId) == FateRuleCollect;

    private bool IsEscortFate(ushort fateId)
        => GetFateRule(fateId) == FateRuleEscort;

    private bool IsDefendFate(ushort fateId)
        => GetFateRule(fateId) == FateRuleDefend;
}
