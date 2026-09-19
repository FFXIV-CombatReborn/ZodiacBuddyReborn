using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXVec3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ZodiacBuddy.Stages.Animus.Data;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private unsafe void ReceiveEventDetour(AddonEvent type, AddonArgs args) {
        try {
            if (args is AddonReceiveEventArgs receiveEventArgs && (AtkEventType)receiveEventArgs.AtkEventType is AtkEventType.ButtonClick) {
                this.ReceiveEvent((AddonRelicNoteBook*)receiveEventArgs.Addon.Address, (AtkEvent*)receiveEventArgs.AtkEvent);
            }
        }
        catch (Exception ex) {
            Service.PluginLog.Error(ex, "[ZodiacBuddy/BOOK] Exception during hook: AddonRelicNotebook.ReceiveEvent:Click");
        }
    }
    private unsafe void ReceiveEvent(AddonRelicNoteBook* addon, AtkEvent* eventData)
    {
        if (!EzThrottler.Throttle("RelicNoteClick", 500))
            return;

        var relicNote = RelicNote.Instance();
        if (relicNote == null)
            return;

        var bookId = relicNote->RelicNoteId;
        var index = addon->CategoryList->SelectedItemIndex;
        var targetComponent = eventData->Target;

        IBraveObjectiveDefinition selectedTarget = targetComponent switch
        {
            // Enemies
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy0.CheckBox) => BraveBook.GetValue(bookId).Enemies[0],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy1.CheckBox) => BraveBook.GetValue(bookId).Enemies[1],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy2.CheckBox) => BraveBook.GetValue(bookId).Enemies[2],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy3.CheckBox) => BraveBook.GetValue(bookId).Enemies[3],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy4.CheckBox) => BraveBook.GetValue(bookId).Enemies[4],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy5.CheckBox) => BraveBook.GetValue(bookId).Enemies[5],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy6.CheckBox) => BraveBook.GetValue(bookId).Enemies[6],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy7.CheckBox) => BraveBook.GetValue(bookId).Enemies[7],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy8.CheckBox) => BraveBook.GetValue(bookId).Enemies[8],
            _ when index == 0 && IsOwnerNode(targetComponent, addon->Enemy9.CheckBox) => BraveBook.GetValue(bookId).Enemies[9],
            // Dungeons
            _ when index == 1 && IsOwnerNode(targetComponent, addon->Dungeon0.CheckBox) => BraveBook.GetValue(bookId).Dungeons[0],
            _ when index == 1 && IsOwnerNode(targetComponent, addon->Dungeon1.CheckBox) => BraveBook.GetValue(bookId).Dungeons[1],
            _ when index == 1 && IsOwnerNode(targetComponent, addon->Dungeon2.CheckBox) => BraveBook.GetValue(bookId).Dungeons[2],
            // FATEs
            _ when index == 2 && IsOwnerNode(targetComponent, addon->Fate0.CheckBox) => BraveBook.GetValue(bookId).Fates[0],
            _ when index == 2 && IsOwnerNode(targetComponent, addon->Fate1.CheckBox) => BraveBook.GetValue(bookId).Fates[1],
            _ when index == 2 && IsOwnerNode(targetComponent, addon->Fate2.CheckBox) => BraveBook.GetValue(bookId).Fates[2],
            // Leves
            _ when index == 3 && IsOwnerNode(targetComponent, addon->Leve0.CheckBox) => BraveBook.GetValue(bookId).Leves[0],
            _ when index == 3 && IsOwnerNode(targetComponent, addon->Leve1.CheckBox) => BraveBook.GetValue(bookId).Leves[1],
            _ when index == 3 && IsOwnerNode(targetComponent, addon->Leve2.CheckBox) => BraveBook.GetValue(bookId).Leves[2],
            _ => throw new ArgumentException($"Unexpected index and/or node: {index}, {(nint)targetComponent:X}"),
        };

        var zoneName = !string.IsNullOrEmpty(selectedTarget.LocationName)
            ? $"{selectedTarget.LocationName}, {selectedTarget.ZoneName}"
            : selectedTarget.ZoneName;

        {
            var sb = new SeStringBuilder()
                .AddText("Target selected: ")
                .AddUiForeground(selectedTarget.Name, 62);

            if (selectedTarget is LeveObjectiveDefinition leveTarget)
                sb.AddText($" from {leveTarget.Issuer}");

            sb.AddText($" in {zoneName}.");

            Service.Plugin.PrintStepProgress(sb.BuiltString);
        }

        if (Service.Configuration.BraveCopyTarget)
        {
            Service.Plugin.PrintMessage($"Copied {selectedTarget.Name} to clipboard.");
            ImGui.SetClipboardText(selectedTarget.Name);

        }
        StopFateGrindingInternal(true);
        CancelBookAutomationForManualObjective();

        switch (index, selectedTarget)
        {
            case (0, EnemyObjectiveDefinition enemyTarget):
                StartEnemyAutomation(enemyTarget, bookId);
                return;
            case (1, DungeonObjectiveDefinition dungeonTarget):
                StartDungeonAutomation(dungeonTarget, bookId, false);
                return;
            case (2, FateObjectiveDefinition fateTarget):
                StartFateAutomation(fateTarget, bookId, false);
                return;
            case (3, LeveObjectiveDefinition leveTarget):
                StartLeveAutomation(leveTarget, bookId, false);
                return;
            default:
                throw new InvalidOperationException($"Unexpected objective type for category {index}: {selectedTarget.GetType().Name}");
        }
    }
}
