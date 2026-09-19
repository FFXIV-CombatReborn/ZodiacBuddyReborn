using System;
using ZodiacBuddy.Stages.Animus;

namespace ZodiacBuddy.Systems.Fates;

internal enum FateGrinderRsrRestoreResult
{
    Restored,
    NoBackup,
    Conflict,
    Failed,
}

internal static class FateGrinderRsrPresetManager
{
    internal const string DesiredHostileType = "AllTargetsCanAttack";
    internal const bool DesiredIgnoreNonFateInFate = true;
    internal const bool DesiredForlornPriority = true;

    internal static RotationSolverGrinderSettings DesiredSettings
        => new(DesiredHostileType, DesiredIgnoreNonFateInFate, DesiredForlornPriority);

    internal static bool TryReadCurrent(out RotationSolverGrinderSettings settings, out string error)
        => RSRIPC.TryReadGrinderSettings(out settings, out error);

    internal static bool AreDesiredSettingsActive(out string detail)
    {
        if (!TryReadCurrent(out var current, out var error))
        {
            detail = error;
            return false;
        }

        if (!Matches(current, DesiredSettings))
        {
            detail = $"Current: {Format(current)}. Required: {Format(DesiredSettings)}.";
            return false;
        }

        detail = "RSR preset ready.";
        return true;
    }

    internal static bool TryApply(out string message)
    {
        var config = Service.Configuration.FateGrinder;
        var backup = config.RsrSettingsBackup;
        if (backup.IsValid)
        {
            if (!backup.HasForlornPrioritySnapshot)
                return TryUpgradeLegacyBackupAndApply(backup, out message);

            message = "An RSR restore point is already active. Restore or discard it before applying again.";
            return false;
        }

        if (!TryReadCurrent(out var original, out var readError))
        {
            message = $"Could not read current RSR settings. Nothing changed. {readError}";
            return false;
        }

        var desired = DesiredSettings;
        if (Matches(original, desired))
        {
            message = "RSR already matches the ZBR preset.";
            return true;
        }

        backup.Capture(original, desired);
        Service.Configuration.Save();

        var writeError = string.Empty;
        var verifyError = string.Empty;
        RotationSolverGrinderSettings verified = default;
        if (!TryWrite(desired, includeForlornPriority: true, out writeError)
            || !TryReadCurrent(out verified, out verifyError)
            || !Matches(verified, desired))
        {
            var failure = !string.IsNullOrWhiteSpace(writeError)
                ? writeError
                : !string.IsNullOrWhiteSpace(verifyError)
                    ? verifyError
                    : "RSR did not report the requested settings after the write.";

            if (TryWrite(original, includeForlornPriority: true, out _)
                && TryReadCurrent(out var restored, out _)
                && Matches(restored, original))
            {
                backup.Clear();
                Service.Configuration.Save();
                message = $"Preset apply failed; original RSR settings were restored. {failure}";
                return false;
            }

            Service.Configuration.Save();
            message = $"Preset apply/rollback could not be verified. The original RSR values remain saved in ZBR. {failure}";
            return false;
        }

        Service.PluginLog.Verbose("[ZodiacBuddy/FATE-GRINDER-RSR] Preset applied; restore point saved.");
        message = "ZBR RSR preset applied. Previous values saved for restore.";
        return true;
    }

    private static bool TryUpgradeLegacyBackupAndApply(FateGrinderRsrSettingsBackup backup, out string message)
    {
        if (!TryReadCurrent(out var current, out var readError))
        {
            message = $"Could not upgrade the existing RSR restore point. Nothing changed. {readError}";
            return false;
        }

        if (!MatchesLegacyApplied(current, backup))
        {
            message = "The existing RSR restore point predates Forlorn priority, and RSR changed since it was applied. Restore or discard the old restore point first.";
            return false;
        }

        backup.OriginalForlornPriority = current.ForlornPriority;
        backup.AppliedForlornPriority = DesiredForlornPriority;
        backup.HasForlornPrioritySnapshot = true;
        Service.Configuration.Save();

        var desired = DesiredSettings;
        var writeError = string.Empty;
        var verifyError = string.Empty;
        RotationSolverGrinderSettings verified = default;
        if (!TryWrite(desired, includeForlornPriority: true, out writeError)
            || !TryReadCurrent(out verified, out verifyError)
            || !Matches(verified, desired))
        {
            var failure = !string.IsNullOrWhiteSpace(writeError)
                ? writeError
                : !string.IsNullOrWhiteSpace(verifyError)
                    ? verifyError
                    : "RSR did not report the upgraded preset after the write.";
            message = $"Could not upgrade the active RSR preset. The complete restore point was kept. {failure}";
            return false;
        }

        Service.PluginLog.Verbose("[ZodiacBuddy/FATE-GRINDER-RSR] Legacy restore point upgraded for Forlorn priority.");
        message = "RSR preset upgraded; Forlorn priority is now enabled and the original value was added to the restore point.";
        return true;
    }

    internal static FateGrinderRsrRestoreResult TryRestore(bool force, out string message)
    {
        var backup = Service.Configuration.FateGrinder.RsrSettingsBackup;
        if (!backup.IsValid)
        {
            message = "No saved RSR restore point.";
            return FateGrinderRsrRestoreResult.NoBackup;
        }

        var original = new RotationSolverGrinderSettings(
            backup.OriginalHostileType,
            backup.OriginalIgnoreNonFateInFate,
            backup.OriginalForlornPriority);
        var applied = new RotationSolverGrinderSettings(
            backup.AppliedHostileType,
            backup.AppliedIgnoreNonFateInFate,
            backup.AppliedForlornPriority);

        if (!force)
        {
            if (!TryReadCurrent(out var current, out var readError))
            {
                message = $"Could not verify current RSR settings. Nothing changed. {readError}";
                return FateGrinderRsrRestoreResult.Failed;
            }

            var matchesApplied = backup.HasForlornPrioritySnapshot
                ? Matches(current, applied)
                : MatchesLegacyApplied(current, backup);
            if (!matchesApplied)
            {
                message = backup.HasForlornPrioritySnapshot
                    ? $"RSR changed after ZBR applied its preset. Current: {Format(current)}. Saved original: {Format(original)}."
                    : $"RSR changed after the older ZBR preset was applied. Current: Hostile={current.HostileType}, IgnoreNonFATE={OnOff(current.IgnoreNonFateInFate)}.";
                return FateGrinderRsrRestoreResult.Conflict;
            }
        }

        if (!TryWrite(original, backup.HasForlornPrioritySnapshot, out var writeError))
        {
            message = $"Could not restore RSR settings. Restore point kept. {writeError}";
            return FateGrinderRsrRestoreResult.Failed;
        }

        if (!TryReadCurrent(out var verified, out var verifyError))
        {
            message = $"RSR restore was issued but could not be verified. Restore point kept. {verifyError}";
            return FateGrinderRsrRestoreResult.Failed;
        }

        var restored = backup.HasForlornPrioritySnapshot
            ? Matches(verified, original)
            : MatchesLegacyOriginal(verified, backup);
        if (!restored)
        {
            message = $"RSR restore did not verify. Restore point kept. Current: {Format(verified)}.";
            return FateGrinderRsrRestoreResult.Failed;
        }

        backup.Clear();
        Service.Configuration.Save();
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-GRINDER-RSR] Saved settings restored; forced={force}.");
        message = "Previous RSR settings restored.";
        return FateGrinderRsrRestoreResult.Restored;
    }

    internal static void KeepCurrentAndClearBackup()
    {
        var backup = Service.Configuration.FateGrinder.RsrSettingsBackup;
        if (!backup.IsValid)
            return;

        backup.Clear();
        Service.Configuration.Save();
        Service.PluginLog.Verbose("[ZodiacBuddy/FATE-GRINDER-RSR] Restore point cleared; current settings kept.");
    }

    private static bool TryWrite(RotationSolverGrinderSettings settings, bool includeForlornPriority, out string error)
    {
        error = string.Empty;
        if (!RSRIPC.TrySetSetting("HostileType", settings.HostileType))
        {
            error = $"Could not set RSR HostileType={settings.HostileType}.";
            return false;
        }

        if (!RSRIPC.TrySetSetting("IgnoreNonFateInFate", settings.IgnoreNonFateInFate.ToString()))
        {
            error = $"Could not set RSR IgnoreNonFateInFate={settings.IgnoreNonFateInFate}.";
            return false;
        }

        if (includeForlornPriority
            && !RSRIPC.TrySetSetting("ForlornPriority", settings.ForlornPriority.ToString()))
        {
            error = $"Could not set RSR ForlornPriority={settings.ForlornPriority}.";
            return false;
        }

        return true;
    }

    private static bool Matches(RotationSolverGrinderSettings left, RotationSolverGrinderSettings right)
        => string.Equals(left.HostileType, right.HostileType, StringComparison.OrdinalIgnoreCase)
            && left.IgnoreNonFateInFate == right.IgnoreNonFateInFate
            && left.ForlornPriority == right.ForlornPriority;

    private static bool MatchesLegacyApplied(RotationSolverGrinderSettings current, FateGrinderRsrSettingsBackup backup)
        => string.Equals(current.HostileType, backup.AppliedHostileType, StringComparison.OrdinalIgnoreCase)
            && current.IgnoreNonFateInFate == backup.AppliedIgnoreNonFateInFate;

    private static bool MatchesLegacyOriginal(RotationSolverGrinderSettings current, FateGrinderRsrSettingsBackup backup)
        => string.Equals(current.HostileType, backup.OriginalHostileType, StringComparison.OrdinalIgnoreCase)
            && current.IgnoreNonFateInFate == backup.OriginalIgnoreNonFateInFate;

    internal static string Format(RotationSolverGrinderSettings settings)
        => $"Hostile={settings.HostileType}, IgnoreNonFATE={OnOff(settings.IgnoreNonFateInFate)}, Forlorn={OnOff(settings.ForlornPriority)}";

    private static string OnOff(bool value) => value ? "On" : "Off";
}
