using System.Collections.Generic;
using System.Numerics;

namespace ZodiacBuddy.Systems.Leves;

internal enum LeveExecutionKind
{
    StandardCombat,
    Rounds,
    BeckonEscort,
    DefendCharge,
    TimedCull,
    ImpostorCull,
    LureAndKill,
    Necrologos,
}

internal readonly record struct LeveExecutionSpec(
    uint LeveId,
    LeveExecutionKind Kind,
    string? PriorityTargetName = null,
    string? ObjectiveObjectName = null,
    string? ProtectedChargeName = null,
    Vector3? ProtectedHoldPosition = null,
    uint EventItemId = 0,
    string? ItemSourceName = null,
    string? PrimeTargetName = null,
    string? EmergedTargetName = null);

internal static class LeveExecutionProfiles
{
    private static readonly IReadOnlyDictionary<uint, LeveExecutionSpec> Specs = new Dictionary<uint, LeveExecutionSpec>
    {
        [643] = new(643, LeveExecutionKind.StandardCombat),
        [644] = new(644, LeveExecutionKind.Necrologos, ObjectiveObjectName: "Parchment"),
        [645] = new(645, LeveExecutionKind.LureAndKill, EventItemId: 2000872, ItemSourceName: "balor's bell", PrimeTargetName: "prime location", EmergedTargetName: "balor"),
        [646] = new(646, LeveExecutionKind.Rounds, ObjectiveObjectName: "Destination"),
        [647] = new(647, LeveExecutionKind.BeckonEscort),
        [649] = new(649, LeveExecutionKind.Necrologos, ObjectiveObjectName: "Parchment"),
        [650] = new(650, LeveExecutionKind.LureAndKill, EventItemId: 2000880, ItemSourceName: "downcast hippocerf", PrimeTargetName: "prime location", EmergedTargetName: "stegotaur"),
        [652] = new(652, LeveExecutionKind.Rounds, ObjectiveObjectName: "Destination"),
        [657] = new(657, LeveExecutionKind.Necrologos, ObjectiveObjectName: "Parchment"),
        [658] = new(658, LeveExecutionKind.LureAndKill, EventItemId: 2000880, ItemSourceName: "ragged hippogryph", PrimeTargetName: "prime location", EmergedTargetName: "Foul River hapalit"),
        [659] = new(659, LeveExecutionKind.Rounds, ObjectiveObjectName: "Destination"),
        [848] = new(848, LeveExecutionKind.StandardCombat, PriorityTargetName: "Mimas"),
        [849] = new(849, LeveExecutionKind.ImpostorCull),
        [853] = new(853, LeveExecutionKind.TimedCull),
        [855] = new(855, LeveExecutionKind.DefendCharge, ProtectedChargeName: "soldiers' effects"),
        [859] = new(859, LeveExecutionKind.ImpostorCull),
        [860] = new(860, LeveExecutionKind.StandardCombat, PriorityTargetName: "frost aevis"),
        [863] = new(863, LeveExecutionKind.TimedCull),
        [865] = new(865, LeveExecutionKind.DefendCharge, ProtectedChargeName: "airship wreckage"),
        [868] = new(868, LeveExecutionKind.DefendCharge, ProtectedChargeName: "research document"),
        [870] = new(870, LeveExecutionKind.TimedCull),
        [873] = new(873, LeveExecutionKind.StandardCombat, PriorityTargetName: "Okeanos the Red"),
        [875] = new(875, LeveExecutionKind.DefendCharge, ProtectedHoldPosition: new Vector3(86.878f, -4.935f, -468.179f)),
    };

    internal static bool TryGet(uint leveId, out LeveExecutionSpec spec)
        => Specs.TryGetValue(leveId, out spec);

    internal static LeveExecutionSpec Get(uint leveId)
        => Specs[leveId];

    internal static string GetName(uint leveId)
        => TryGet(leveId, out var spec)
            ? spec.Kind switch
            {
                LeveExecutionKind.StandardCombat => "standard-combat",
                LeveExecutionKind.Rounds => "rounds-combat",
                LeveExecutionKind.BeckonEscort => "beckon-escort",
                LeveExecutionKind.DefendCharge => "defend-charge",
                LeveExecutionKind.TimedCull => "timed-cull",
                LeveExecutionKind.ImpostorCull => "impostor-cull",
                LeveExecutionKind.LureAndKill => "lure-and-kill",
                LeveExecutionKind.Necrologos => "necrologos",
                _ => "unknown",
            }
            : "unsupported";
}
