using System;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using ZodiacBuddy.Stages.Novus.Data;

namespace ZodiacBuddy.Stages.Novus;

/// <summary>
/// Handles Mysterious Map automation for the Novus stage.
///
/// Feature 1 - Decipher context menu:
///   Right-click Mysterious Map in inventory -> "Decipher Map" appears.
///   Clicking it fires the Decipher general action.
///
/// Feature 2 - Navigate to dig spot:
///   When AutoNavigateAfterMapUse is enabled and the Alexandrite Map hint window
///   opens, a small overlay presents both possible spots for the shown zone.
///   The player picks the one matching the X in the image; the plugin then
///   teleports, mounts, flies to the spot and optionally uses Dig on arrival.
/// </summary>
internal sealed class NovusMapManager : IDisposable
{
    private const uint MysteriousMapItemId = 7884u;
    private const uint DecipherActionId    = 19u;
    private const uint DigActionId         = 20u;
    private const uint MountRouletteId     =  9u;

    private readonly TaskManager _taskManager = new();
    private readonly AlexandritePickerWindow _pickerWindow;

    // Navigation state
    private bool _hasEnteredBetweenAreas;
    private bool _awaitingTeleport;
    private bool _hasQueuedMountTasks;

    /// <summary>
    /// Initializes a new instance of the <see cref="NovusMapManager"/> class.
    /// </summary>
    public NovusMapManager()
    {
        _pickerWindow = new AlexandritePickerWindow(StartNavigation);
        Service.ContextMenu.OnMenuOpened += OnMenuOpened;
        // TODO: Narrow this to the specific addon name shown when an Alexandrite Map hint
        //       is revealed (verify the addon name in-game or via datamining before adding).
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnHintWindowSetup);
    }

    private static NovusConfiguration Config => Service.Configuration.Novus;

    /// <summary>Gets the picker window for registration with the window system.</summary>
    internal AlexandritePickerWindow PickerWindow => _pickerWindow;

    /// <inheritdoc/>
    public void Dispose()
    {
        Service.ContextMenu.OnMenuOpened -= OnMenuOpened;
        Service.AddonLifecycle.UnregisterListener(OnHintWindowSetup);
        Svc.Framework.Update -= WaitForArrivalAndNavigate;
        _pickerWindow.Dispose();
        _taskManager.Abort();
    }

    // --- Feature 1: Context menu + SelectString auto-confirm ---------------

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory { TargetItem: { } item }) return;
        if (item.ItemId != MysteriousMapItemId) return;

        args.AddMenuItem(new MenuItem
        {
            Name      = new SeString(new TextPayload("Decipher Map")),
            OnClicked = OnDecipherClicked,
            IsEnabled = CanAct(),
        });
    }

    private unsafe void OnDecipherClicked(IMenuItemClickedArgs _)
    {
        var am = ActionManager.Instance();
        if (am->GetActionStatus(ActionType.GeneralAction, DecipherActionId) != 0)
        {
            ZodiacBuddyPlugin.PrintError("Decipher is not available right now.");
            return;
        }

        am->UseAction(ActionType.GeneralAction, DecipherActionId);
        Service.Plugin.PrintMessage("Deciphering Mysterious Map...");
    }

    // --- Feature 2: Hint window detection + spot-picker overlay -----------

    /// <summary>
    /// Fires when one of the candidate hint-window addons opens.
    /// Reads the zone name from the addon's text nodes and activates the picker.
    /// </summary>
    private unsafe void OnHintWindowSetup(AddonEvent addonEvent, AddonArgs args)
    {
        var addonBase = (AtkUnitBase*)(nint)args.Addon;

        if (!Config.AutoNavigateAfterMapUse)
            return;

        // Scan all text nodes; if none match a known zone name this is not a treasure map hint.
        var zoneName = FindZoneNameInAddon(addonBase);
        if (zoneName == null)
            return;

        if (!AlexandriteMapData.ByZoneName.TryGetValue(zoneName, out var spots))
        {
            Service.PluginLog.Warning($"[NovusMap] Zone \"{zoneName}\" not in Alexandrite map data.");
            return;
        }

        _pickerWindow.Show(spots[0], spots[1], zoneName);
    }

    /// <summary>
    /// Walks every AtkTextNode in the addon and returns the first text that
    /// matches a known Alexandrite Map zone name.
    /// </summary>
    private static unsafe string? FindZoneNameInAddon(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (NodeType)node->Type != NodeType.Text) continue;

            var text = ((AtkTextNode*)node)->NodeText.ToString().Trim();
            if (text.Length == 0) continue;

            // The game truncates long zone names in the text node (e.g. "Coerthas Central Highland"
            // instead of "Coerthas Central Highlands"), so we check both directions:
            // text contains key (exact/superset) OR key starts with text (truncated).
            // Minimum length of 8 avoids false positives from short strings like "1".
            if (text.Length >= 8)
            {
                // Strip trailing "..." that the game appends when truncating long zone names.
                var stripped = text.TrimEnd('.');

                foreach (var key in AlexandriteMapData.ByZoneName.Keys)
                {
                    if (text.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                        key.StartsWith(stripped, StringComparison.OrdinalIgnoreCase))
                        return key;
                }
            }
        }
        return null;
    }

    // --- Navigation -------------------------------------------------------

    private void StartNavigation(AlexandriteSpot spot)
    {
        var mapLink = spot.ToMapLink();

        var aetheryteId = Util.GetNearestAetheryte(mapLink);
        if (aetheryteId == 0)
        {
            ZodiacBuddyPlugin.PrintError("Could not find a nearby aetheryte for this location.");
            return;
        }

        Service.GameGui.OpenMapWithMapLink(mapLink);
        Service.Plugin.PrintMessage(
            $"Navigating to {spot.ZoneName} ({spot.MapX:F1}, {spot.MapY:F1})...");

        _hasEnteredBetweenAreas = false;
        _awaitingTeleport       = true;
        _hasQueuedMountTasks    = false;

        var attempts   = 0;
        var retryAfter = DateTime.MinValue;
        _taskManager.Enqueue(() =>
        {
            var c = Svc.Condition;
            if (c[ConditionFlag.InCombat] || c[ConditionFlag.BetweenAreas]) return false;
            if (DateTime.Now < retryAfter) return false;
            if (!CanAct()) return false;

            unsafe
            {
                if (!Telepo.Instance()->Teleport(aetheryteId, 0))
                {
                    if (++attempts >= 5)
                    {
                        Service.PluginLog.Warning(
                            $"[NovusMap] Teleport to {aetheryteId} failed after {attempts} attempts.");
                        _awaitingTeleport = false;
                        return true;
                    }
                    retryAfter = DateTime.Now.AddSeconds(2);
                    return false;
                }
            }
            return true;
        }, 120_000, "NovusMapTeleport");

        Svc.Framework.Update += WaitForArrivalAndNavigate;
    }

    private void WaitForArrivalAndNavigate(IFramework _)
    {
        if (!_awaitingTeleport) return;

        if (!_hasEnteredBetweenAreas)
        {
            if (Svc.Condition[ConditionFlag.BetweenAreas])
                _hasEnteredBetweenAreas = true;
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
        if (!ECommons.GenericHelpers.IsScreenReady()) return;
        if (_hasQueuedMountTasks) return;

        _hasQueuedMountTasks = true;
        _awaitingTeleport    = false;
        Svc.Framework.Update -= WaitForArrivalAndNavigate;

        EnqueueMountAndFlyToFlag();
    }

    private unsafe void EnqueueMountAndFlyToFlag()
    {
        _taskManager.Enqueue(() => VNavmesh.Nav.IsReady());

        _taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted]) return true;
            var am = ActionManager.Instance();
            if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteId) != 0) return false;
            am->UseAction(ActionType.GeneralAction, MountRouletteId);
            return true;
        }, 30_000, "NovusMapMount");

        _taskManager.Enqueue(() => Svc.Condition[ConditionFlag.Mounted]);

        _taskManager.Enqueue(() =>
        {
            Chat.ExecuteCommand("/vnav flyflag");
            _taskManager.DelayNextImmediate(2000);
            return true;
        });

        EnqueueNavWaitDismountAndDig();
    }

    private unsafe void EnqueueNavWaitDismountAndDig()
    {
        var am = ActionManager.Instance();

        // Wait for vnavmesh to finish pathing.
        _taskManager.Enqueue(() =>
        {
            if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
                return false;
            return true;
        }, 300_000, "NovusMapNavComplete");

        // Step 1: initial dismount press.
        _taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0u);
        }, "NovusMapDismount1");

        // Step 2: wait until no longer in flight (landed but may still be mounted briefly).
        _taskManager.Enqueue(() =>
            !Svc.Condition[ConditionFlag.InFlight] && CanAct(),
            10_000, "NovusMapWaitLanded");

        // Step 3: second dismount press in case the first only cancelled flight.
        _taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0u);
        }, "NovusMapDismount2");

        // Step 4: wait until fully dismounted and able to act.
        _taskManager.Enqueue(() =>
            !Svc.Condition[ConditionFlag.Mounted] && CanAct(),
            10_000, "NovusMapWaitDismounted");

        // Step 5: short settle delay.
        _taskManager.Enqueue(() => { _taskManager.DelayNextImmediate(750); return true; });

        if (Config.AutoDigAfterNavigation)
        {
            _taskManager.Enqueue(() =>
            {
                if (am->GetActionStatus(ActionType.GeneralAction, DigActionId) != 0)
                    return false;
                am->UseAction(ActionType.GeneralAction, DigActionId);
                Service.Plugin.PrintMessage("Digging for treasure...");
                return true;
            }, 15_000, "NovusMapDig");
        }
    }

    // --- Helpers ----------------------------------------------------------

    private static bool CanAct()
    {
        if (Player.Object is null || Player.Object.IsDead) return false;
        var c = Svc.Condition;
        return !c[ConditionFlag.BetweenAreas]
            && !c[ConditionFlag.BetweenAreas51]
            && !c[ConditionFlag.Casting]
            && !c[ConditionFlag.Casting87]
            && !c[ConditionFlag.OccupiedInQuestEvent]
            && !c[ConditionFlag.Unconscious];
    }
}
