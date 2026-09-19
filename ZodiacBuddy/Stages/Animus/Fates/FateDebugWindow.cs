using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class FateDebugWindow : Window
{
    private readonly AnimusAutomationFacade automation;
    private string filter = string.Empty;
    private uint selectedFateId;
    private ushort selectedLiveFateId;
    private bool grindWhenUnavailable = true;

    internal FateDebugWindow(AnimusAutomationFacade automation) : base("ZodiacBuddy FATE Test Harness")
    {
        this.automation = automation;
        Size = new Vector2(920f, 760f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void Draw()
    {
        var snapshot = automation.GetFateDebugSnapshot();
        var grinder = automation.GetFateGrindingSnapshot();

        ImGui.TextWrapped("Exercises the same FATE executor used by Animus automation. Selected Trials FATE tests can optionally grind other live FATEs while the requested FATE is absent. Continuous general grinding and persistent FATE exclusions now live in the production grinder window.");
        ImGui.Spacing();

        DrawExecutionStatus(snapshot, grinder);
        ImGui.Separator();
        DrawBookTargetHarness(snapshot, grinder);
        ImGui.Separator();
        DrawLiveFateHarness(snapshot, grinder);
    }

    private void DrawExecutionStatus(FateAutomationSnapshot snapshot, FateGrindingSnapshot grinder)
    {
        if (!ImGui.CollapsingHeader("Execution / grinder state", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Text($"Executor: {snapshot.State} | Purpose: {snapshot.Purpose}");
        if (snapshot.TargetFateId != 0)
            ImGui.Text($"Requested FATE: {snapshot.TargetName} ({snapshot.TargetFateId})");
        if (snapshot.WorkingFateId != 0)
            ImGui.Text($"Working FATE: {FateMetadata.GetName(snapshot.WorkingFateId)} ({snapshot.WorkingFateId}){(snapshot.WorkingPrerequisite ? " [prerequisite]" : string.Empty)}");
        ImGui.TextWrapped($"Executor status: {snapshot.Status}");

        if (snapshot.RuntimeResult.Kind != FateExecutionResultKind.None)
            ImGui.TextWrapped($"Runtime result: {snapshot.RuntimeResult.Kind} - {snapshot.RuntimeResult.Status}");
        if (snapshot.FinalResult.Kind != FateExecutionResultKind.None)
            ImGui.TextWrapped($"Final result: {snapshot.FinalResult.Kind} - {snapshot.FinalResult.Status}");

        ImGui.Spacing();
        ImGui.Text($"Grinder: {(grinder.Active ? grinder.Mode.ToString() : "Inactive")}");
        if (grinder.DesiredFateId != 0)
            ImGui.Text($"Desired FATE: {FateMetadata.GetName(grinder.DesiredFateId)} ({grinder.DesiredFateId})");
        if (grinder.ActiveFillerFateId != 0)
            ImGui.Text($"Active filler: {FateMetadata.GetName(grinder.ActiveFillerFateId)} ({grinder.ActiveFillerFateId})");
        ImGui.Text($"Filler attempts this session: {grinder.FillerAttempts}");
        ImGui.TextWrapped($"Grinder status: {grinder.Status}");
        if (grinder.LastResult.Kind != FateExecutionResultKind.None)
            ImGui.TextWrapped($"Last filler/target result: {grinder.LastResult.Kind} - {grinder.LastResult.Status}");

        var executorActive = !IsExecutorTerminal(snapshot.State);
        if (executorActive && ImGui.Button("Stop active FATE run"))
            automation.CancelFateDebugRun();

        if (grinder.Active)
        {
            if (executorActive)
                ImGui.SameLine();
            if (ImGui.Button("Stop grinder"))
                automation.StopFateGrinding();
        }
    }

    private void DrawBookTargetHarness(FateAutomationSnapshot snapshot, FateGrindingSnapshot grinder)
    {
        if (!ImGui.CollapsingHeader("Trials of the Braves target tests", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Checkbox("Grind local FATEs while selected target is unavailable", ref grindWhenUnavailable);
        ImGui.TextDisabled("When enabled, the harness probes the selected FATE first. If absent, it executes experimental filler FATEs in that target's territory and preempts them as soon as the selected FATE or its prerequisite becomes actionable.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##FateFilter", "Filter by FATE name, zone, or ID", ref filter, 128);

        var allTargets = automation.GetDebugFateTargets();
        var targets = allTargets.Where(target => MatchesFilter(target, filter)).ToArray();
        if (ImGui.BeginChild("BookFateList", new Vector2(0f, 180f), true))
        {
            foreach (var target in targets)
            {
                var prerequisite = FateMetadata.GetPrerequisite(checked((ushort)target.FateId));
                var label = prerequisite == 0
                    ? $"{target.Name}  [{target.FateId}]  -  {target.ZoneName}"
                    : $"{target.Name}  [{target.FateId}]  -  {target.ZoneName}  (chain: {FateMetadata.GetName(prerequisite)} [{prerequisite}])";
                if (ImGui.Selectable(label, selectedFateId == target.FateId))
                    selectedFateId = target.FateId;
            }
        }
        ImGui.EndChild();

        var selected = allTargets.FirstOrDefault(target => target.FateId == selectedFateId);
        if (selected.FateId == 0)
        {
            ImGui.TextDisabled("Select one of the Trials of the Braves FATEs above.");
            return;
        }

        DrawBookTargetDetails(selected);
        var canStart = !grinder.Active && IsExecutorTerminal(snapshot.State);
        if (canStart)
        {
            var label = grindWhenUnavailable ? "Start selected FATE test + grind fallback" : "Start selected FATE test";
            if (ImGui.Button(label))
            {
                if (grindWhenUnavailable)
                    automation.StartDebugFateWithGrinding(selected.FateId);
                else
                    automation.StartDebugFate(selected.FateId);
            }
            ImGui.SameLine();
            if (ImGui.Button("Open selected map marker"))
                Service.GameGui.OpenMapWithMapLink(selected.Position);
        }
    }

    private static void DrawBookTargetDetails(FateObjectiveDefinition selected)
    {
        var fateId = checked((ushort)selected.FateId);
        var prereqId = FateMetadata.GetPrerequisite(fateId);
        ImGui.Text($"Selected: {selected.Name} ({selected.FateId}) - {selected.ZoneName}");
        if (prereqId != 0)
            ImGui.Text($"Prerequisite: {FateMetadata.GetName(prereqId)} ({prereqId})");
        if (FateMetadata.GetFallbackSpawnerName(prereqId) is { } prerequisiteSpawner)
            ImGui.Text($"Prerequisite starter NPC: {prerequisiteSpawner}");
        if (FateMetadata.GetFallbackSpawnerName(fateId) is { } targetSpawner)
            ImGui.Text($"Target starter NPC: {targetSpawner}");
        if (FateMetadata.IsEscort(fateId))
            ImGui.TextDisabled("Known handling: escort-anchor follow.");
        else if (FateMetadata.GetPrimaryCombatTargetName(fateId) is { } primaryCombatTarget)
        {
            if (FateMetadata.GetSecondaryCombatTargetName(fateId) is { } secondaryCombatTarget)
                ImGui.TextDisabled($"Known handling: priority combat targets '{primaryCombatTarget}' > '{secondaryCombatTarget}'.");
            else
                ImGui.TextDisabled($"Known handling: priority combat target '{primaryCombatTarget}'.");
        }
        else if (FateMetadata.PrefersEventObjects(fateId))
            ImGui.TextDisabled("Known handling: FATE objective objects are prioritized.");
    }

    private void DrawLiveFateHarness(FateAutomationSnapshot snapshot, FateGrindingSnapshot grinder)
    {
        if (!ImGui.CollapsingHeader("Live FATE discovery / general grinder", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("This section exposes every live FATE currently visible to Dalamud in the territory for discovery diagnostics and one-off execution tests. Continuous general grinding now lives in the production FATE grinder window.");
        ImGui.Spacing();

        var executorBusy = !IsExecutorTerminal(snapshot.State);
#if DEBUG
        if (ImGui.Button("Open production FATE grinder"))
            Service.Plugin.OpenFateGrinderWindow();
        ImGui.TextDisabled("Continuous general grinding and persistent FATE exclusions are configured in the production grinder window (/zgrind). This harness remains for diagnostics and one-off execution tests.");
#endif

        var live = automation.GetLiveFateDiagnostics();
        ImGui.Text($"Live FATEs exposed: {live.Count}");
        if (ImGui.BeginChild("LiveFateList", new Vector2(0f, 210f), true))
        {
            foreach (var fate in live)
            {
                var distance = fate.Distance == float.MaxValue ? "?" : $"{fate.Distance:F1}y";
                var mineGround = UsesUGhamaroGroundTravel(fate);
                var flags = fate.IsCurrentFate ? " [CURRENT]" : fate.IsRequiredTarget ? " [BOOK]" : fate.IsKnownPrerequisite ? " [PREREQ]" : mineGround ? " [MINE-GROUND]" : string.Empty;
                var label = $"{fate.Name} [{fate.FateId}] {fate.State} {fate.Progress}% {distance}{flags} | {fate.RuleName} | E:{fate.TaggedEnemies} F:{fate.TaggedFriendlies} O:{fate.TaggedEventObjects}";
                if (ImGui.Selectable(label, selectedLiveFateId == fate.FateId))
                    selectedLiveFateId = fate.FateId;
            }
        }
        ImGui.EndChild();

        var selected = live.FirstOrDefault(fate => fate.FateId == selectedLiveFateId);
        if (selected.FateId == 0)
        {
            ImGui.TextDisabled("Select a live FATE to inspect its discovery data or run it once.");
            return;
        }

        ImGui.Text($"{selected.Name} ({selected.FateId}) | State={selected.State} | Progress={selected.Progress}% | HandIn={selected.HandInCount}");
        ImGui.Text($"Rule={selected.RuleName} ({selected.RuleRaw}) | FateRuleEx={selected.FateRuleEx}");
        ImGui.Text($"Level={selected.Level}-{selected.MaxLevel} | Remaining={selected.TimeRemainingSeconds}s / Duration={selected.DurationSeconds}s | StartEpoch={selected.StartTimeEpoch} | Bonus={selected.HasBonus}");
        ImGui.Text($"Territory={selected.TerritoryTypeId} | Icon={selected.IconId} | MapIcon={selected.MapIconId}");
        ImGui.Text($"Position=({selected.Position.X:F1}, {selected.Position.Y:F1}, {selected.Position.Z:F1}) | Radius={selected.Radius:F1} | Distance={(selected.Distance == float.MaxValue ? "?" : $"{selected.Distance:F1}y")}");
        ImGui.Text($"Current={selected.IsCurrentFate} | BookTarget={selected.IsRequiredTarget} | KnownPrerequisite={selected.IsKnownPrerequisite} | KnownSpecialHandling={selected.HasKnownSpecialHandling} | U'GhamaroGroundTravel={UsesUGhamaroGroundTravel(selected)}");
        ImGui.TextWrapped($"Objective: {selected.Objective}");
        ImGui.TextWrapped($"Description: {selected.Description}");
        ImGui.Text($"Tagged actors: enemies={selected.TaggedEnemies}, friendlies={selected.TaggedFriendlies}, eventObjects={selected.TaggedEventObjects}, other={selected.TaggedOtherObjects}");
        ImGui.TextWrapped($"Tagged actor identities: {selected.TaggedActorSummary}");
        ImGui.Text($"MotivationNpc: {selected.MotivationNpcId}");
        ImGui.TextWrapped($"MotivationNpc object: {selected.MotivationNpcSummary}");
        ImGui.Text($"ObjectiveNpc: {selected.ObjectiveNpcId}");
        ImGui.TextWrapped($"ObjectiveNpc object: {selected.ObjectiveNpcSummary}");
        ImGui.Text($"SheetEventItem={selected.SheetEventItemId} x{selected.SheetEventItemCount} | EventItem={selected.EventItemId} x{selected.EventItemCount}");
        ImGui.Text($"ReqEventItem={selected.RequiredEventItemId} x{selected.RequiredEventItemCount} | TurnInEventItem={selected.TurnInEventItemId} x{selected.TurnInEventItemCount}");
        ImGui.Text($"Objective markers: {selected.ObjectiveMarkerCount}");
        ImGui.TextWrapped($"Objective marker data: {selected.ObjectiveMarkerSummary}");

        if (ImGui.Button("Copy selected FATE diagnostics"))
            ImGui.SetClipboardText(FormatDiagnostic(selected));
        ImGui.SameLine();
        if (ImGui.Button("Copy all live FATE diagnostics"))
            ImGui.SetClipboardText(string.Join("\n\n", live.Select(FormatDiagnostic)));

        if (!grinder.Active && !executorBusy && ImGui.Button("Run selected live FATE once"))
            automation.StartDebugLiveFate(selected.FateId);
    }

    private static bool IsExecutorTerminal(FateAutomationState state)
        => state is FateAutomationState.Idle
            or FateAutomationState.Completed
            or FateAutomationState.Unavailable
            or FateAutomationState.RetryLater
            or FateAutomationState.Failed
            or FateAutomationState.Preempted
            or FateAutomationState.Cancelled;

    private static string FormatDiagnostic(FateRuntimeDiagnostic fate)
        => $"FateId={fate.FateId} Name='{fate.Name}' Territory={fate.TerritoryTypeId} State={fate.State} Progress={fate.Progress}% HandIn={fate.HandInCount} Rule={fate.RuleName}({fate.RuleRaw}) FateRuleEx={fate.FateRuleEx} " +
           $"StartEpoch={fate.StartTimeEpoch} Duration={fate.DurationSeconds}s Remaining={fate.TimeRemainingSeconds}s Bonus={fate.HasBonus} Level={fate.Level}-{fate.MaxLevel} Icon={fate.IconId} MapIcon={fate.MapIconId} " +
           $"Position=({fate.Position.X:F1},{fate.Position.Y:F1},{fate.Position.Z:F1}) Radius={fate.Radius:F1} Distance={(fate.Distance == float.MaxValue ? "?" : $"{fate.Distance:F1}y")} " +
           $"Current={fate.IsCurrentFate} BookTarget={fate.IsRequiredTarget} KnownPrerequisite={fate.IsKnownPrerequisite} KnownSpecialHandling={fate.HasKnownSpecialHandling} UGhamaroGroundTravel={UsesUGhamaroGroundTravel(fate)} " +
           $"TaggedEnemies={fate.TaggedEnemies} TaggedFriendlies={fate.TaggedFriendlies} TaggedEventObjects={fate.TaggedEventObjects} TaggedOther={fate.TaggedOtherObjects} " +
           $"MotivationNpc={fate.MotivationNpcId} MotivationNpcObject=[{fate.MotivationNpcSummary}] ObjectiveNpc={fate.ObjectiveNpcId} ObjectiveNpcObject=[{fate.ObjectiveNpcSummary}] " +
           $"SheetEventItem={fate.SheetEventItemId}x{fate.SheetEventItemCount} EventItem={fate.EventItemId}x{fate.EventItemCount} ReqEventItem={fate.RequiredEventItemId}x{fate.RequiredEventItemCount} TurnInEventItem={fate.TurnInEventItemId}x{fate.TurnInEventItemCount} " +
           $"ObjectiveMarkers={fate.ObjectiveMarkerCount} [{fate.ObjectiveMarkerSummary}] Objective=[{fate.Objective}] Description=[{fate.Description}] TaggedActors=[{fate.TaggedActorSummary}]";

    private static bool UsesUGhamaroGroundTravel(FateRuntimeDiagnostic fate)
        => fate.TerritoryTypeId == OuterLaNosceaTravelPolicy.TerritoryTypeId
            && fate.Position != Vector3.Zero
            && OuterLaNosceaTravelPolicy.IsUGhamaroWorldLocation(fate.Position);

    private static bool MatchesFilter(FateObjectiveDefinition target, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var query = value.Trim();
        return target.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || target.ZoneName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || target.FateId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
