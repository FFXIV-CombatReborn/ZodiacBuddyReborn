using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;
using System.Numerics;
using ECommons.GameHelpers;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class FateGrinderWindow : Window
{
    private readonly AnimusAutomationFacade automation;
    private readonly FateGrinderMultiPullWindow multiPullWindow;
    private int manualFateId;
    private bool rsrRestoreConflict;
    private string rsrPresetMessage = string.Empty;

    internal FateGrinderWindow(AnimusAutomationFacade automation, FateGrinderMultiPullWindow multiPullWindow) : base("ZodiacBuddy FATE Grinder")
    {
        this.automation = automation;
        this.multiPullWindow = multiPullWindow;
        Size = new Vector2(720f, 620f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void Draw()
    {
        var grinder = automation.GetFateGrindingSnapshot();
        var executor = automation.GetFateDebugSnapshot();
        var generalActive = grinder.Active && grinder.Mode == FateGrindingMode.General;

        DrawControls(grinder, executor, generalActive);
        ImGui.Separator();
        DrawSelectionPolicy();
        ImGui.Separator();
        DrawTravelPolicy();
        ImGui.Separator();
        DrawCombatPolicy(generalActive);
        ImGui.Separator();
        DrawLiveFates(grinder);
        ImGui.Separator();
        DrawBlacklist();
    }

    private void DrawControls(FateGrindingSnapshot grinder, FateAutomationSnapshot executor, bool generalActive)
    {
        var state = grinder.Active ? grinder.Mode.ToString() : "Inactive";
        ImGui.Text($"Territory: {Service.ClientState.TerritoryType}   Grinder: {state}   Attempts: {grinder.FillerAttempts}");

        if (grinder.ActiveFillerFateId != 0)
            ImGui.Text($"Current: {FateMetadata.GetName(grinder.ActiveFillerFateId)} [{grinder.ActiveFillerFateId}]");

        if (!string.IsNullOrWhiteSpace(grinder.Status))
            ImGui.TextDisabled($"Status: {grinder.Status}");

        if (grinder.LastResult.Kind != FateExecutionResultKind.None)
            ImGui.TextDisabled($"Last: {grinder.LastResult.Kind} - {grinder.LastResult.Status}");

        ImGui.Spacing();
        if (generalActive)
        {
            if (ImGui.Button("Stop Grinder"))
                automation.StopGeneralFateGrinding();
        }
        else if (!grinder.Active && IsExecutorTerminal(executor.State))
        {
            if (ImGui.Button("Start Grinder"))
                automation.StartGeneralFateGrinding();
            ImGui.SameLine();
            ImGui.TextDisabled("Current territory");
        }
        else if (grinder.Active)
        {
            ImGui.TextDisabled("Another FATE mode is active.");
        }
        else
        {
            ImGui.TextDisabled("FATE executor is busy.");
        }
    }

    private static void DrawSelectionPolicy()
    {
        if (!ImGui.CollapsingHeader("Selection", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var config = Service.Configuration.FateGrinder;

        var minimumRemaining = config.MinimumTimeRemainingSeconds;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("Min remaining (s)", ref minimumRemaining, 15, 60))
        {
            config.MinimumTimeRemainingSeconds = Math.Clamp(minimumRemaining, 0, FateGrinderConfiguration.MaximumConfigurableTimeRemainingSeconds);
            Service.Configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"0 = off | default {FateGrinderConfiguration.RecommendedMinimumTimeRemainingSeconds}");

        var maximumProgress = config.MaximumProgressPercent;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("Max progress (%)", ref maximumProgress, 1, 5))
        {
            config.MaximumProgressPercent = Math.Clamp(maximumProgress, 0, 100);
            Service.Configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"100 = off | default {FateGrinderConfiguration.RecommendedMaximumProgressPercent}");

        var preferBonus = config.PreferBonusFates;
        if (ImGui.Checkbox("Prefer bonus FATEs", ref preferBonus))
        {
            config.PreferBonusFates = preferBonus;
            Service.Configuration.Save();
        }

        if (ImGui.Button("Reset Selection"))
        {
            config.ResetRecommendedSelectionDefaults();
            Service.Configuration.Save();
        }
    }

    private static void DrawTravelPolicy()
    {
        if (!ImGui.CollapsingHeader("Travel", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var config = Service.Configuration.FateGrinder;
        var useTeleports = config.UseTeleportsWhenBeneficial;
        if (ImGui.Checkbox("Use beneficial aetheryte teleports", ref useTeleports))
        {
            config.UseTeleportsWhenBeneficial = useTeleports;
            Service.Configuration.Save();
        }

        if (Service.Configuration.DisableTeleport)
            ImGui.TextDisabled("Disabled by global Disable Teleport.");

        if (ImGui.Button("Reset Travel"))
        {
            config.ResetRecommendedTravelDefaults();
            Service.Configuration.Save();
        }
    }

    private void DrawCombatPolicy(bool generalActive)
    {
        if (!ImGui.CollapsingHeader("Experimental Combat", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var config = Service.Configuration.FateGrinder;
        var experimental = config.ExperimentalCombat;
        if (ImGui.Checkbox("Enable experimental grinder combat", ref experimental))
        {
            config.ExperimentalCombat = experimental;
            Service.Configuration.Save();
        }
        ImGui.TextDisabled("Generic: Nearest | Boss: HighMaxHP | Special/profile FATEs: legacy");

        var multiPull = config.MultiPullEnabled;
        if (ImGui.Checkbox("Job-aware multi-pull", ref multiPull))
        {
            config.MultiPullEnabled = multiPull;
            Service.Configuration.Save();
        }

        var classJobId = Player.Object?.ClassJob.RowId ?? 0;
        var packSize = FateGrinderMultiPullProfiles.GetPackSize(config, classJobId);
        ImGui.SameLine();
        ImGui.TextDisabled($"{FateGrinderMultiPullProfiles.GetAbbreviation(classJobId)} • Pack {packSize} • Radius {config.EffectiveMultiPullRadius:F0}y");
        if (ImGui.Button("Configure Jobs..."))
            multiPullWindow.IsOpen = true;

        ImGui.Spacing();
        ImGui.Text("RSR Preset");
        ImGui.TextDisabled(
            $"ZBR: Hostile={FateGrinderRsrPresetManager.DesiredHostileType} | " +
            $"Ignore non-FATE={OnOff(FateGrinderRsrPresetManager.DesiredIgnoreNonFateInFate)} | " +
            $"Forlorn={OnOff(FateGrinderRsrPresetManager.DesiredForlornPriority)}");

        if (FateGrinderRsrPresetManager.TryReadCurrent(out var current, out var readError))
            ImGui.Text($"Current: {FateGrinderRsrPresetManager.Format(current)}");
        else
            ImGui.TextDisabled($"Current: unavailable ({readError})");

        var backup = config.RsrSettingsBackup;
        if (backup.IsValid)
        {
            var forlorn = backup.HasForlornPrioritySnapshot
                ? OnOff(backup.OriginalForlornPriority)
                : "unchanged (legacy backup)";
            ImGui.TextDisabled(
                $"Restore point: Hostile={backup.OriginalHostileType} | " +
                $"Ignore non-FATE={OnOff(backup.OriginalIgnoreNonFateInFate)} | Forlorn={forlorn}");
        }

        if (generalActive)
            ImGui.TextDisabled("Stop Grinder to change persistent RSR settings.");

        ImGui.BeginDisabled(generalActive);
        if (!backup.IsValid)
        {
            if (ImGui.Button("Apply ZBR RSR Preset"))
            {
                rsrRestoreConflict = false;
                FateGrinderRsrPresetManager.TryApply(out rsrPresetMessage);
            }
        }
        else
        {
            if (!backup.HasForlornPrioritySnapshot)
            {
                if (ImGui.Button("Upgrade ZBR RSR Preset"))
                {
                    rsrRestoreConflict = false;
                    FateGrinderRsrPresetManager.TryApply(out rsrPresetMessage);
                }
                ImGui.SameLine();
            }

            if (ImGui.Button("Restore RSR Settings"))
            {
                var result = FateGrinderRsrPresetManager.TryRestore(false, out rsrPresetMessage);
                rsrRestoreConflict = result == FateGrinderRsrRestoreResult.Conflict;
            }

            if (rsrRestoreConflict)
            {
                ImGui.TextWrapped(rsrPresetMessage);
                if (ImGui.Button("Restore Anyway"))
                {
                    var result = FateGrinderRsrPresetManager.TryRestore(true, out rsrPresetMessage);
                    rsrRestoreConflict = result == FateGrinderRsrRestoreResult.Conflict;
                }
                ImGui.SameLine();
                if (ImGui.Button("Keep Current"))
                {
                    FateGrinderRsrPresetManager.KeepCurrentAndClearBackup();
                    rsrRestoreConflict = false;
                    rsrPresetMessage = "Current RSR settings kept; restore point cleared.";
                }
            }
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(rsrPresetMessage) && !rsrRestoreConflict)
            ImGui.TextDisabled(rsrPresetMessage);

        if (config.ExperimentalCombat && !FateGrinderRsrPresetManager.AreDesiredSettingsActive(out var presetDetail))
            ImGui.TextWrapped($"Fallback to legacy combat: {presetDetail}");
    }

    private void DrawLiveFates(FateGrindingSnapshot grinder)
    {
        var live = automation.GetLiveFateDiagnostics();
        if (!ImGui.CollapsingHeader($"Live FATEs ({live.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!ImGui.BeginChild("GrinderLiveFates", new Vector2(0f, 230f), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var fate in live)
        {
            var blacklisted = Service.Configuration.FateGrinderBlacklist.Contains(fate.FateId);
            ImGui.PushID(fate.FateId);
            if (ImGui.Checkbox("##Blacklist", ref blacklisted))
                SetBlacklisted(fate.FateId, blacklisted);
            ImGui.SameLine();

            var distance = fate.Distance == float.MaxValue ? "?" : $"{fate.Distance:F1}y";
            var remaining = FormatRemainingTime(fate.TimeRemainingSeconds);
            var flags = fate.IsRequiredTarget
                ? " [BOOK]"
                : fate.IsKnownPrerequisite
                    ? " [PREREQ]"
                    : string.Empty;
            var active = grinder.ActiveFillerFateId == fate.FateId ? " [ACTIVE]" : string.Empty;
            ImGui.Text($"{fate.Name} [{fate.FateId}] | {fate.State} {fate.Progress}% | {remaining} | {distance}{flags}{active}");
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private void DrawBlacklist()
    {
        if (!ImGui.CollapsingHeader("Blacklist"))
            return;

        ImGui.TextDisabled("Optional/general FATEs only; required book work ignores this list.");
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("FATE ID", ref manualFateId, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Add"))
        {
            if (manualFateId is > 0 and <= ushort.MaxValue)
            {
                SetBlacklisted((ushort)manualFateId, true);
                manualFateId = 0;
            }
            else
            {
                ZodiacBuddyPlugin.PrintError("Enter a valid FATE ID between 1 and 65535.");
            }
        }

        var blacklist = Service.Configuration.FateGrinderBlacklist
            .OrderBy(id => FateMetadata.GetName(id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id)
            .ToArray();

        if (blacklist.Length == 0)
        {
            ImGui.TextDisabled("None");
            return;
        }

        if (ImGui.BeginChild("FateGrinderBlacklist", new Vector2(0f, 150f), true))
        {
            foreach (var fateId in blacklist)
            {
                ImGui.PushID(fateId);
                if (ImGui.SmallButton("Remove"))
                {
                    SetBlacklisted(fateId, false);
                    ImGui.PopID();
                    break;
                }
                ImGui.SameLine();
                ImGui.Text($"{FateMetadata.GetName(fateId)} [{fateId}]");
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private static void SetBlacklisted(ushort fateId, bool blacklisted)
    {
        var changed = blacklisted
            ? Service.Configuration.FateGrinderBlacklist.Add(fateId)
            : Service.Configuration.FateGrinderBlacklist.Remove(fateId);
        if (!changed)
            return;

        Service.Configuration.Save();
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-GRINDER] FateId={fateId} '{FateMetadata.GetName(fateId)}' user blacklist={(blacklisted ? "enabled" : "disabled")}.");
    }

    private static string FormatRemainingTime(long seconds)
    {
        if (seconds < 0)
            return "?";

        var remaining = TimeSpan.FromSeconds(seconds);
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static bool IsExecutorTerminal(FateAutomationState state)
        => state is FateAutomationState.Idle
            or FateAutomationState.Completed
            or FateAutomationState.Unavailable
            or FateAutomationState.RetryLater
            or FateAutomationState.Failed
            or FateAutomationState.Preempted
            or FateAutomationState.Cancelled;
}
