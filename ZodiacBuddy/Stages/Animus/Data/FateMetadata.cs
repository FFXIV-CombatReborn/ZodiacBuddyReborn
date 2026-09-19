using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Excel.Sheets;
using System;

namespace ZodiacBuddy.Stages.Animus.Data;

internal static class FateMetadata
{
    internal static ushort GetPrerequisite(ushort fateId)
        => fateId switch
        {
            569 => 568,
            571 => 570,
            611 => 610,
            _ => 0,
        };

    internal static string? GetFallbackSpawnerName(ushort fateId)
        => fateId switch
        {
            486 => "House Haillenarte Guard",
            587 => "Storm Private",
            610 => "Mianne Thousandmalm",
            642 => "Wary Merchant",
            _ => null,
        };

    internal static bool IsEscort(ushort fateId)
        => fateId == 642;

    internal static string? GetEscortNpcName(ushort fateId)
        => fateId switch
        {
            642 => "Wary Merchant",
            _ => null,
        };

    internal static bool PrefersEventObjects(ushort fateId)
        => false;

    internal static string? GetPrimaryCombatTargetName(ushort fateId)
        => fateId switch
        {
            587 => "Kobold Toolbox",
            633 => "Ixali Swiftbeak",
            _ => null,
        };

    internal static string? GetSecondaryCombatTargetName(ushort fateId)
        => fateId switch
        {
            633 => "Airstone",
            _ => null,
        };

    internal static MapLinkPayload GetStagingMapLink(ushort fateId, FateObjectiveDefinition target)
        => fateId switch
        {
            568 => new MapLinkPayload(138, 18, 20.0f, 19.0f),
            570 => new MapLinkPayload(138, 18, 18.0f, 22.0f),
            610 => new MapLinkPayload(152, 5, 27.0f, 21.0f),
            _ when fateId == target.FateId => target.Position,
            _ => throw new ArgumentOutOfRangeException(nameof(fateId), fateId, "No staging location is registered for this FATE."),
        };

    internal static string GetName(ushort fateId)
    {
        try
        {
            var row = Service.DataManager.GetExcelSheet<Fate>().GetRow(fateId);
            var name = row.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) ? $"FATE {fateId}" : name;
        }
        catch
        {
            return $"FATE {fateId}";
        }
    }
}
