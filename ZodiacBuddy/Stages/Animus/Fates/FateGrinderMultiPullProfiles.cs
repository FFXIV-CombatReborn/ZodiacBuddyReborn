using System;

namespace ZodiacBuddy.Stages.Animus;

internal enum FateGrinderCombatRole
{
    Tank,
    Healer,
    Melee,
    PhysicalRanged,
    Caster,
    Limited,
}

internal readonly record struct FateGrinderMultiPullJobDefinition(
    uint ClassJobId,
    string Abbreviation,
    FateGrinderCombatRole Role,
    int DefaultPackSize);

internal static class FateGrinderMultiPullProfiles
{
    internal const int MinimumPackSize = 1;
    internal const int MaximumPackSize = 8;
    internal const int MinimumPullRadius = 8;
    internal const int MaximumPullRadius = 25;
    internal const int DefaultPullRadius = 18;
    internal const int MinimumHpFloorPercent = 10;
    internal const int MaximumHpFloorPercent = 95;
    internal const int DefaultHpFloorPercent = 55;

    internal static readonly FateGrinderMultiPullJobDefinition[] Jobs =
    [
        new(19, "PLD", FateGrinderCombatRole.Tank, 4),
        new(21, "WAR", FateGrinderCombatRole.Tank, 5),
        new(32, "DRK", FateGrinderCombatRole.Tank, 4),
        new(37, "GNB", FateGrinderCombatRole.Tank, 4),

        new(24, "WHM", FateGrinderCombatRole.Healer, 2),
        new(28, "SCH", FateGrinderCombatRole.Healer, 2),
        new(33, "AST", FateGrinderCombatRole.Healer, 2),
        new(40, "SGE", FateGrinderCombatRole.Healer, 3),

        new(20, "MNK", FateGrinderCombatRole.Melee, 3),
        new(22, "DRG", FateGrinderCombatRole.Melee, 3),
        new(30, "NIN", FateGrinderCombatRole.Melee, 3),
        new(34, "SAM", FateGrinderCombatRole.Melee, 2),
        new(39, "RPR", FateGrinderCombatRole.Melee, 3),
        new(41, "VPR", FateGrinderCombatRole.Melee, 3),

        new(23, "BRD", FateGrinderCombatRole.PhysicalRanged, 3),
        new(31, "MCH", FateGrinderCombatRole.PhysicalRanged, 3),
        new(38, "DNC", FateGrinderCombatRole.PhysicalRanged, 4),

        new(25, "BLM", FateGrinderCombatRole.Caster, 2),
        new(27, "SMN", FateGrinderCombatRole.Caster, 3),
        new(35, "RDM", FateGrinderCombatRole.Caster, 3),
        new(42, "PCT", FateGrinderCombatRole.Caster, 3),

        new(36, "BLU", FateGrinderCombatRole.Limited, 4),
    ];

    internal static int GetPackSize(FateGrinderConfiguration config, uint classJobId)
    {
        config.MultiPullPackSizeByJob ??= new();
        if (config.MultiPullPackSizeByJob.TryGetValue(classJobId, out var configured))
            return Math.Clamp(configured, MinimumPackSize, MaximumPackSize);

        foreach (var job in Jobs)
        {
            if (job.ClassJobId == classJobId)
                return job.DefaultPackSize;
        }
        return 2;
    }

    internal static string GetAbbreviation(uint classJobId)
    {
        foreach (var job in Jobs)
        {
            if (job.ClassJobId == classJobId)
                return job.Abbreviation;
        }

        return classJobId == 0 ? "?" : $"Job {classJobId}";
    }

    internal static void SetPackSize(FateGrinderConfiguration config, uint classJobId, int packSize)
    {
        config.MultiPullPackSizeByJob ??= new();
        config.MultiPullPackSizeByJob[classJobId] = Math.Clamp(packSize, MinimumPackSize, MaximumPackSize);
    }

    internal static void ResetJobDefaults(FateGrinderConfiguration config)
    {
        config.MultiPullPackSizeByJob ??= new();
        config.MultiPullPackSizeByJob.Clear();
    }
}
