using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using FFXIVGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using GameRelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using ZodiacBuddy.Systems.Fates;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal sealed unsafe class LeveDebugWindow : Window
{
    private readonly AnimusAutomationFacade automation;
    private const uint LeveMarkerIconId = 60492;
    private const uint LeveEnemyNameplateIconId = 71244;
    private string filter = string.Empty;
    private uint selectedLeveId;

    internal LeveDebugWindow(AnimusAutomationFacade automation) : base("ZodiacBuddy Leve Test Harness")
    {
        this.automation = automation;
        Size = new Vector2(900f, 720f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    internal void SelectLeve(uint leveId)
    {
        if (automation.GetDebugLeveTargets().Any(target => target.LeveId == leveId))
            selectedLeveId = leveId;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Diagnostics for the Animus leve pipeline. Select one of the book leves, travel to its issuer with the existing ZBR leve navigation, then use the live sections below while opening the leve vendor, initiating the leve, and entering combat.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##LeveFilter", "Filter by leve name, issuer, zone, or ID", ref filter, 128);
        ImGui.Spacing();

        var targets = automation.GetDebugLeveTargets()
            .Where(target => MatchesFilter(target, filter))
            .ToArray();

        if (ImGui.BeginChild("LeveList", new Vector2(0f, 210f), true))
        {
            foreach (var target in targets)
            {
                var label = $"{target.Name}  [{target.LeveId}]  -  {target.Issuer}  -  {target.ZoneName}";
                if (ImGui.Selectable(label, selectedLeveId == target.LeveId))
                    selectedLeveId = target.LeveId;
            }
        }
        ImGui.EndChild();

        var selected = automation.GetDebugLeveTargets().FirstOrDefault(target => target.LeveId == selectedLeveId);
        if (selected.LeveId == 0)
        {
            ImGui.TextDisabled("Select one of the unique Trials of the Braves leves above.");
            return;
        }

        ImGui.Text($"Selected: {selected.Name} ({selected.LeveId})");
        ImGui.Text($"Issuer: {selected.Issuer} | Zone: {selected.ZoneName}");
        ImGui.Text($"Dataset slot from representative book: {selected.LeveSlot}");
        ImGui.Text($"Execution profile: {LeveExecutionProfiles.GetName(selected.LeveId)}");

        if (ImGui.Button("Travel to selected issuer"))
            automation.StartDebugLeveTravel(selected.LeveId);
        ImGui.SameLine();
        if (ImGui.Button("Open issuer map marker"))
            Service.GameGui.OpenMapWithMapLink(selected.Position);
        ImGui.SameLine();
        if (ImGui.Button("Dump diagnostics to log"))
            DumpDiagnostics(selected);

        var automationSnapshot = automation.GetLeveAutomationSnapshot();
        if (LeveExecutionProfiles.TryGet(selected.LeveId, out _))
        {
            if (ImGui.Button($"Run {LeveExecutionProfiles.GetName(selected.LeveId)} automation test"))
                automation.StartDebugLeveAutomation(selected.LeveId);
            ImGui.SameLine();
            if (automationSnapshot.State is not (LeveAutomationState.Idle or LeveAutomationState.Complete or LeveAutomationState.Failed)
                && ImGui.Button("Cancel automation test"))
                automation.CancelDebugLeveAutomation();
        }

        if (automationSnapshot.State != LeveAutomationState.Idle)
        {
            ImGui.Text($"Automation: {automationSnapshot.State} | LeveId={automationSnapshot.LeveId} | visited markers={automationSnapshot.VisitedMarkers} | target={automationSnapshot.CombatTargetId} | RSR owned={automationSnapshot.RsrOwned}");
            ImGui.TextWrapped(automationSnapshot.Status);
        }

        ImGui.Separator();
        DrawRunState(selected);
        DrawBookState(selected);
        DrawAcceptedLeves(selected);
        DrawIssuerObjects(selected);
        DrawVendorMenu();
        DrawVendorOfferings(selected);
        DrawJournalState();
        DrawYesNoState();
        DrawLeveMarkers(selected);
        DrawLeveEnemies(selected);
    }

    private static void DrawRunState(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("Player / leve state", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var player = Player.Object;
        ImGui.Text($"Territory: {Svc.ClientState.TerritoryType} | BoundByDuty: {Svc.Condition[ConditionFlag.BoundByDuty]} | Nav ready: {VNavmesh.Nav.IsReady()} | Pathing: {VNavmesh.Path.IsRunning()}");
        if (player != null)
            ImGui.Text($"Player: ({player.Position.X:F1}, {player.Position.Y:F1}, {player.Position.Z:F1}) | Selected issuer marker territory: {selected.Position.TerritoryType.RowId}");

        var questManager = QuestManager.Instance();
        if (questManager != null)
            ImGui.Text($"Leve allowances: {questManager->NumLeveAllowances}");
    }

    private static void DrawBookState(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("RelicNote book state", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var relicNote = GameRelicNote.Instance();
        if (relicNote == null)
        {
            ImGui.TextDisabled("RelicNote unavailable.");
            return;
        }

        ImGui.Text($"Current RelicNoteId: {relicNote->RelicNoteId}");
        if (!BraveBook.TryGetLeveTarget(relicNote->RelicNoteId, selected.LeveId, out var currentBookTarget))
        {
            ImGui.TextDisabled("Selected leve is not part of the currently loaded book.");
            return;
        }

        ImGui.Text($"Current-book slot: {currentBookTarget.LeveSlot} | Book credit: {relicNote->IsLeveComplete(currentBookTarget.LeveSlot)}");
    }

    private static void DrawAcceptedLeves(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("Accepted levequests", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var questManager = QuestManager.Instance();
        if (questManager == null)
        {
            ImGui.TextDisabled("QuestManager unavailable.");
            return;
        }

        var accepted = questManager->LeveQuests.ToArray();
        var any = false;
        foreach (var leve in accepted)
        {
            if (leve.LeveId == 0)
                continue;

            any = true;
            var marker = leve.LeveId == selected.LeveId ? "  <selected>" : string.Empty;
            ImGui.Text($"LeveId={leve.LeveId} sequence={leve.Sequence} flags={leve.Flags} clearClass={leve.ClearClass}{marker}");
        }

        if (!any)
            ImGui.TextDisabled("No accepted levequests reported.");
    }

    private static void DrawIssuerObjects(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("Issuer objects", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var issuerName = GetIssuerName(selected.Issuer);
        var player = Player.Object;
        var found = false;
        foreach (var obj in Svc.Objects)
        {
            if (!obj.Name.TextValue.Equals(issuerName, StringComparison.OrdinalIgnoreCase))
                continue;

            found = true;
            var baseId = obj.Address == nint.Zero ? 0u : ((FFXIVGameObject*)obj.Address)->BaseId;
            var distance = player == null ? -1f : Vector3.Distance(player.Position, obj.Position);
            ImGui.Text($"{obj.Name.TextValue}: gameObjectId={obj.GameObjectId} entityId={obj.EntityId} baseId={baseId} targetable={obj.IsTargetable} distance={distance:F1} position=({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1})");
        }

        if (!found)
            ImGui.TextDisabled($"No loaded object named '{issuerName}'.");
    }

    private static void DrawVendorMenu()
    {
        if (!ImGui.CollapsingHeader("Leve issuer menu (SelectString)", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) || addon == null || !GenericHelpers.IsAddonReady(addon))
        {
            ImGui.TextDisabled("SelectString addon is not open.");
            return;
        }

        var master = new AddonMaster.SelectString(addon);
        var index = 0;
        foreach (var entry in master.Entries)
            ImGui.Text($"[{index++}] '{entry.Text}'");
    }

    private static void DrawVendorOfferings(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("GuildLeve vendor offerings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GuildLeve", out var addon) || addon == null || !GenericHelpers.IsAddonReady(addon))
        {
            ImGui.TextDisabled("GuildLeve addon is not open.");
            return;
        }

        var count = addon->AtkValues[25].UInt;
        ImGui.Text($"Visible offerings: {count}");
        for (var i = 0; i < count; i++)
        {
            var nameValue = addon->AtkValues[626 + i * 2];
            var levelValue = addon->AtkValues[627 + i * 2];
            if (!IsStringValue(nameValue.Type))
                break;

            var name = MemoryHelper.ReadSeStringNullTerminated((nint)nameValue.String.Value).GetText();
            var level = IsStringValue(levelValue.Type)
                ? MemoryHelper.ReadSeStringNullTerminated((nint)levelValue.String.Value).GetText()
                : string.Empty;
            var leveId = ResolveLeveId(name);
            var marker = leveId == selected.LeveId ? "  <selected>" : string.Empty;
            ImGui.Text($"[{i}] LeveId={leveId} level='{level}' name='{name}'{marker}");
        }
    }

    private static void DrawJournalState()
    {
        if (!ImGui.CollapsingHeader("JournalDetail"))
            return;

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("JournalDetail", out var addon) || addon == null || !GenericHelpers.IsAddonReady(addon))
        {
            ImGui.TextDisabled("JournalDetail addon is not open.");
            return;
        }

        var master = new AddonMaster.JournalDetail(addon);
        ImGui.Text($"Can initiate: {master.CanInitiate}");
    }

    private static void DrawYesNoState()
    {
        if (!ImGui.CollapsingHeader("SelectYesno"))
            return;

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon) || addon == null || !GenericHelpers.IsAddonReady(addon))
        {
            ImGui.TextDisabled("SelectYesno addon is not open.");
            return;
        }

        var master = new AddonMaster.SelectYesno(addon);
        ImGui.TextWrapped($"Prompt: {master.Text}");
        ImGui.Text($"Visible buttons: {master.ButtonsVisible}");
    }

    private static void DrawLeveMarkers(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("Leve map markers (60492)"))
            return;

        var hud = AgentHUD.Instance();
        var player = Player.Object;
        if (hud == null)
        {
            ImGui.TextDisabled("AgentHUD unavailable.");
            return;
        }

        var found = false;
        foreach (var marker in hud->MapMarkers)
        {
            if (marker.IconId != LeveMarkerIconId)
                continue;

            found = true;
            var tooltip = marker.TooltipString == null ? string.Empty : marker.TooltipString->ToString();
            var distance = player == null
                ? -1f
                : Vector3.Distance(player.Position, new Vector3(marker.Position.X, marker.Position.Y, marker.Position.Z));
            var exact = tooltip.Equals(selected.Name, StringComparison.OrdinalIgnoreCase) ? "  <selected-name>" : string.Empty;
            ImGui.Text($"tooltip='{tooltip}' radius={marker.Radius:F1} distance={distance:F1} position=({marker.Position.X:F1}, {marker.Position.Y:F1}, {marker.Position.Z:F1}){exact}");
        }

        if (!found)
            ImGui.TextDisabled("No leve markers are currently exposed by AgentHUD.");
    }

    private static void DrawLeveEnemies(LeveObjectiveDefinition selected)
    {
        if (!ImGui.CollapsingHeader("Leve objective / profile actors"))
            return;

        var player = Player.Object;
        var names = GetProfileObjectNames(selected.LeveId);
        var uiState = UIState.Instance();
        Director* activeDirector = uiState == null ? null : uiState->DirectorTodo.Director;
        if (activeDirector != null)
        {
            var directorEventId = activeDirector->Info.EventId;
            ImGui.Text($"Active director: leveId={activeDirector->ContentId} eventId=0x{directorEventId.Id:X8} entry={directorEventId.EntryId} content=0x{(ushort)directorEventId.ContentId:X4} selected={activeDirector->ContentId == selected.LeveId}");
        }
        else
        {
            ImGui.TextDisabled("No active DirectorTodo director.");
        }

        var found = false;
        foreach (var obj in Svc.Objects)
        {
            var expectedName = names.Contains(obj.Name.TextValue);
            if (obj is IBattleNpc npc)
            {
                var director = IsLeveDirectorObject(npc);
                var icon = FateTargeting.GetNameplateIconId(npc);
                if (!director && icon != LeveEnemyNameplateIconId && !expectedName)
                    continue;

                found = true;
                var distance = player == null ? -1f : Vector3.Distance(player.Position, npc.Position);
                var npcEventId = npc.Address == nint.Zero ? default : ((FFXIVGameObject*)npc.Address)->EventId;
                var npcOwnedByActiveDirector = activeDirector != null && npc.Address != nint.Zero && (npcEventId == activeDirector->Info.EventId || ((FFXIVGameObject*)npc.Address)->EventHandler == (FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler*)activeDirector);
                ImGui.Text($"{npc.Name.TextValue}: id={npc.GameObjectId} baseId={npc.BaseId} nameId={npc.NameId} icon={icon} eventId=0x{npcEventId.Id:X8} entry={npcEventId.EntryId} eventContent=0x{(ushort)npcEventId.ContentId:X4} activeDirector={npcOwnedByActiveDirector} hp={npc.CurrentHp}/{npc.MaxHp} targetable={npc.IsTargetable} dead={npc.IsDead} hostile={FateTargeting.IsHostileEnemy(npc)} kind={npc.BattleNpcKind} distance={distance:F1} targetObjectId={npc.TargetObjectId}{(expectedName ? "  <profile>" : string.Empty)}");
                continue;
            }

            if (!expectedName)
                continue;
            found = true;
            var objectDistance = player == null ? -1f : Vector3.Distance(player.Position, obj.Position);
            var objectEventId = obj.Address == nint.Zero ? default : ((FFXIVGameObject*)obj.Address)->EventId;
            var objectOwnedByActiveDirector = activeDirector != null && obj.Address != nint.Zero && (objectEventId == activeDirector->Info.EventId || ((FFXIVGameObject*)obj.Address)->EventHandler == (FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler*)activeDirector);
            ImGui.Text($"{obj.Name.TextValue}: kind={obj.ObjectKind} id={obj.GameObjectId} eventId=0x{objectEventId.Id:X8} entry={objectEventId.EntryId} eventContent=0x{(ushort)objectEventId.ContentId:X4} activeDirector={objectOwnedByActiveDirector} targetable={obj.IsTargetable} distance={objectDistance:F1} position=({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1})  <profile>");
        }

        if (!found)
            ImGui.TextDisabled("No director-linked, 71244-tagged, or profile-named actors are currently loaded.");
    }

    private void DumpDiagnostics(LeveObjectiveDefinition selected)
    {
        var questManager = QuestManager.Instance();
        var allowances = questManager == null ? -1 : questManager->NumLeveAllowances;
        Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Snapshot selectedId={selected.LeveId} name='{selected.Name}' issuer='{selected.Issuer}' zone='{selected.ZoneName}' territory={Svc.ClientState.TerritoryType} allowances={allowances} boundByDuty={Svc.Condition[ConditionFlag.BoundByDuty]}.");

        var uiState = UIState.Instance();
        Director* activeDirector = uiState == null ? null : uiState->DirectorTodo.Director;
        if (activeDirector != null)
        {
            var directorEventId = activeDirector->Info.EventId;
            Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Active director contentId={activeDirector->ContentId} eventId=0x{directorEventId.Id:X8} entry={directorEventId.EntryId} eventContent=0x{(ushort)directorEventId.ContentId:X4} selected={activeDirector->ContentId == selected.LeveId}.");
        }

        if (questManager != null)
        {
            foreach (var leve in questManager->LeveQuests.ToArray())
            {
                if (leve.LeveId != 0)
                    Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Accepted leveId={leve.LeveId} sequence={leve.Sequence} flags={leve.Flags} clearClass={leve.ClearClass} selected={leve.LeveId == selected.LeveId}.");
            }
        }

        var relicNote = GameRelicNote.Instance();
        if (relicNote != null && BraveBook.TryGetLeveTarget(relicNote->RelicNoteId, selected.LeveId, out var currentBookTarget))
            Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Book relicNoteId={relicNote->RelicNoteId} selectedLeveId={selected.LeveId} slot={currentBookTarget.LeveSlot} complete={relicNote->IsLeveComplete(currentBookTarget.LeveSlot)}.");

        var issuerName = GetIssuerName(selected.Issuer);
        var player = Player.Object;
        foreach (var obj in Svc.Objects)
        {
            if (obj.Name.TextValue.Equals(issuerName, StringComparison.OrdinalIgnoreCase))
            {
                var baseId = obj.Address == nint.Zero ? 0u : ((FFXIVGameObject*)obj.Address)->BaseId;
                var distance = player == null ? -1f : Vector3.Distance(player.Position, obj.Position);
                Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Issuer object name='{obj.Name.TextValue}' gameObjectId={obj.GameObjectId} entityId={obj.EntityId} baseId={baseId} targetable={obj.IsTargetable} distance={distance:F1} position={obj.Position}.");
            }

            var profileNames = GetProfileObjectNames(selected.LeveId);
            if (obj is IBattleNpc npc && (FateTargeting.GetNameplateIconId(npc) == LeveEnemyNameplateIconId || IsLeveDirectorObject(npc) || profileNames.Contains(npc.Name.TextValue)))
            {
                var distance = player == null ? -1f : Vector3.Distance(player.Position, npc.Position);
                var npcEventId = npc.Address == nint.Zero ? default : ((FFXIVGameObject*)npc.Address)->EventId;
                var npcOwnedByActiveDirector = activeDirector != null && npc.Address != nint.Zero && (npcEventId == activeDirector->Info.EventId || ((FFXIVGameObject*)npc.Address)->EventHandler == (FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler*)activeDirector);
                Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Objective actor name='{npc.Name.TextValue}' id={npc.GameObjectId} baseId={npc.BaseId} nameId={npc.NameId} icon={FateTargeting.GetNameplateIconId(npc)} eventId=0x{npcEventId.Id:X8} entry={npcEventId.EntryId} eventContent=0x{(ushort)npcEventId.ContentId:X4} activeDirector={npcOwnedByActiveDirector} hp={npc.CurrentHp}/{npc.MaxHp} targetable={npc.IsTargetable} dead={npc.IsDead} hostile={FateTargeting.IsHostileEnemy(npc)} kind={npc.BattleNpcKind} distance={distance:F1} targetObjectId={npc.TargetObjectId} profileName={profileNames.Contains(npc.Name.TextValue)}.");
            }
            else if (profileNames.Contains(obj.Name.TextValue))
            {
                var distance = player == null ? -1f : Vector3.Distance(player.Position, obj.Position);
                var objectEventId = obj.Address == nint.Zero ? default : ((FFXIVGameObject*)obj.Address)->EventId;
                var objectOwnedByActiveDirector = activeDirector != null && obj.Address != nint.Zero && (objectEventId == activeDirector->Info.EventId || ((FFXIVGameObject*)obj.Address)->EventHandler == (FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler*)activeDirector);
                Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Profile object name='{obj.Name.TextValue}' kind={obj.ObjectKind} id={obj.GameObjectId} eventId=0x{objectEventId.Id:X8} entry={objectEventId.EntryId} eventContent=0x{(ushort)objectEventId.ContentId:X4} activeDirector={objectOwnedByActiveDirector} targetable={obj.IsTargetable} distance={distance:F1} position={obj.Position}.");
            }
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var selectString) && selectString != null && GenericHelpers.IsAddonReady(selectString))
        {
            var master = new AddonMaster.SelectString(selectString);
            var index = 0;
            foreach (var entry in master.Entries)
                Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] SelectString index={index++} text='{entry.Text}'.");
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("GuildLeve", out var addon) && addon != null && GenericHelpers.IsAddonReady(addon))
        {
            var count = addon->AtkValues[25].UInt;
            Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] GuildLeve visible count={count}.");
            for (var i = 0; i < count; i++)
            {
                var value = addon->AtkValues[626 + i * 2];
                if (!IsStringValue(value.Type))
                    break;

                var name = MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).GetText();
                var leveId = ResolveLeveId(name);
                Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Offering index={i} leveId={leveId} name='{name}' selected={leveId == selected.LeveId}.");
            }
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("JournalDetail", out var journalDetail) && journalDetail != null && GenericHelpers.IsAddonReady(journalDetail))
            Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] JournalDetail canInitiate={new AddonMaster.JournalDetail(journalDetail).CanInitiate}.");

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesNo) && yesNo != null && GenericHelpers.IsAddonReady(yesNo))
        {
            var yesNoMaster = new AddonMaster.SelectYesno(yesNo);
            Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] SelectYesno prompt='{yesNoMaster.Text}' buttons={yesNoMaster.ButtonsVisible}.");
        }

        var automationSnapshot = automation.GetLeveAutomationSnapshot();
        if (automationSnapshot.State != LeveAutomationState.Idle)
            Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Automation state={automationSnapshot.State} leveId={automationSnapshot.LeveId} visitedMarkers={automationSnapshot.VisitedMarkers} combatTargetId={automationSnapshot.CombatTargetId} rsrOwned={automationSnapshot.RsrOwned} status='{automationSnapshot.Status}'.");

        var hud = AgentHUD.Instance();
        if (hud != null)
        {
            foreach (var marker in hud->MapMarkers)
            {
                if (marker.IconId != LeveMarkerIconId)
                    continue;

                var tooltip = marker.TooltipString == null ? string.Empty : marker.TooltipString->ToString();
                Service.PluginLog.Debug($"[ZodiacBuddy/LEVE] Marker icon={marker.IconId} tooltip='{tooltip}' radius={marker.Radius:F1} position=({marker.Position.X:F1},{marker.Position.Y:F1},{marker.Position.Z:F1}) selectedName={tooltip.Equals(selected.Name, StringComparison.OrdinalIgnoreCase)}.");
            }
        }
    }

    private static System.Collections.Generic.HashSet<string> GetProfileObjectNames(uint leveId)
    {
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!LeveExecutionProfiles.TryGet(leveId, out var spec))
            return names;
        foreach (var name in new[] { spec.PriorityTargetName, spec.ObjectiveObjectName, spec.ProtectedChargeName, spec.ItemSourceName, spec.PrimeTargetName, spec.EmergedTargetName })
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        if (spec.Kind == LeveExecutionKind.BeckonEscort)
            names.Add("Mine Hound");
        return names;
    }

    private static bool IsLeveDirectorObject(IGameObject obj)
    {
        if (obj.Address == nint.Zero)
            return false;
        var content = ((FFXIVGameObject*)obj.Address)->EventId.ContentId;
        return content is EventHandlerContent.BattleLeveDirector or EventHandlerContent.CompanyLeveDirector;
    }

    private static uint ResolveLeveId(string name)
    {
        foreach (var leve in Service.DataManager.GetExcelSheet<Leve>())
        {
            if (leve.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase))
                return leve.RowId;
        }

        return 0;
    }

    private static string GetIssuerName(string issuer)
    {
        var suffix = issuer.IndexOf(" (", StringComparison.Ordinal);
        return suffix < 0 ? issuer : issuer[..suffix];
    }

    private static bool IsStringValue(ValueType type)
        => type is ValueType.String or ValueType.ManagedString or ValueType.ConstString;

    private static bool MatchesFilter(LeveObjectiveDefinition target, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var query = value.Trim();
        return target.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || target.Issuer.Contains(query, StringComparison.OrdinalIgnoreCase)
            || target.ZoneName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || target.LeveId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
