using System;

namespace ZodiacBuddy.Stages.Animus.Data;

internal static class BraveLeveCatalog
{
    internal static string GetIssuer(uint leveId) {
        var (gcId, issuerName) = leveId switch {
            643 => (0, "Rurubana"),
            644 => (0, "Rurubana"),
            645 => (0, "Rurubana"),
            646 => (0, "Rurubana"),
            647 => (0, "Rurubana"),
            649 => (0, "Voilinaut"),
            650 => (0, "Voilinaut"),
            652 => (0, "Voilinaut"),
            657 => (0, "K'leytai"),
            658 => (0, "K'leytai"),
            659 => (0, "K'leytai"),
            848 => (1, "Lodille"),
            849 => (1, "Lodille"),
            853 => (2, "Lodille"),
            855 => (2, "Lodille"),
            859 => (3, "Lodille"),
            860 => (3, "Lodille"),
            863 => (1, "Eidhart"),
            865 => (1, "Eidhart"),
            868 => (2, "Eidhart"),
            870 => (2, "Eidhart"),
            875 => (3, "Eidhart"),
            873 => (3, "Eidhart"),
            _ => throw new ArgumentException($"Unregistered leve: {leveId}"),
        };

        var gcName =
            gcId switch {
                1 => "Maelstrom",
                2 => "Order of the Twin Adder",
                3 => "Immortal Flames",
                _ => string.Empty,
            };

        if (gcName != string.Empty)
            issuerName += $" ({gcName})";

        return issuerName;
    }
}
