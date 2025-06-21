using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.Commands;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
///using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
//using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using ImGuizmoNET;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ZodiacBuddy.Stages.Atma.Data;
using ZodiacBuddy.Stages.Atma.Movement;
using ZodiacBuddy.Stages.Atma.Unstuck;
using static FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.Delegates;
using static FFXIVClientStructs.Havok.Animation.Deform.Skinning.hkaMeshBinding;
using static ZodiacBuddy.TargetWindow.TargetInfoWindow;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma;
/// <summary>
/// Your buddy for the Atma enhancement stage.
/// </summary>
internal class AtmaManager : IDisposable {
    /// <summary>
    /// Initializes a new instance of the <see cref="AtmaManager"/> class.
    /// </summary>
    
    private Vector3 _lastPosition;
    private DateTime _lastMovement = DateTime.Now;
    private const float MinMovementDistance = 0.2f; // Adjust as needed
    private const float NavResetThreshold = 3f;     // Seconds before declaring stuck
    private readonly AdvancedUnstuck _advancedUnstuck;
    public static System.Action? OnFallbackPathIssued;
    private bool monitoringPathing = false;
    private DateTime unmountStartTime;
    private bool monitoringUnstuck = false;
    private bool restartNavAfterUnstuck = false;
    private bool hasQueuedMountTasks = false;
    private bool hasEnteredBetweenAreas = false;
    private bool awaitingTeleportFromRelicBookClick = false;
    public bool IsPathGenerating => VNavmesh.Nav.PathfindInProgress();
    public bool IsPathing => VNavmesh.Path.IsRunning();
    public bool NavReady => VNavmesh.Nav.IsReady();
    private List<Vector3>? _lastKnownPath;
    private readonly TaskManager TaskManager = new();
    public bool CanAct
    {
        get
        {
            var player = Svc.ClientState.LocalPlayer;
            if (player == null || player.IsDead || Player.IsAnimationLocked)
                return false;
            var c = Svc.Condition;
            if (c[ConditionFlag.BetweenAreas]
                || c[ConditionFlag.BetweenAreas51]
                || c[ConditionFlag.OccupiedInQuestEvent]
                || c[ConditionFlag.OccupiedSummoningBell]
                || c[ConditionFlag.BeingMoved]
                || c[ConditionFlag.Casting]
                || c[ConditionFlag.Casting87]
                || c[ConditionFlag.Jumping]
                || c[ConditionFlag.Jumping61]
                || c[ConditionFlag.LoggingOut]
                || c[ConditionFlag.Occupied]
                || c[ConditionFlag.Occupied39]
                || c[ConditionFlag.Unconscious]
                || c[ConditionFlag.ExecutingGatheringAction]
                || c[ConditionFlag.MountOrOrnamentTransition]
                || c[85] && !c[ConditionFlag.Gathering])
                return false;
            return true;
        }
    }
    public AtmaManager() 
    {
        _advancedUnstuck = new AdvancedUnstuck();
        _advancedUnstuck.OnUnstuckComplete += OnUnstuckCompleteHandler;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", ReceiveEventDetour);
        Svc.Framework.Update += _advancedUnstuck.RunningUpdate;
        Svc.Framework.Update += MonitorUnstuck;
    }
    /// <inheritdoc/>
    public void Dispose() {
        Svc.Framework.Update -= MonitorUnstuck;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        Svc.Framework.Update -= _advancedUnstuck.RunningUpdate;
        Service.AddonLifecycle.UnregisterListener(ReceiveEventDetour);
    }
    private static uint GetNearestAetheryte(MapLinkPayload mapLink) {
        var closestAetheryteId = 0u;
        var closestDistance = double.MaxValue;

        static float ConvertRawPositionToMapCoordinate(int pos, float scale) {
            var c = scale / 100.0f;
            var scaledPos = pos * c / 1000.0f;

            return (41.0f / c * ((scaledPos + 1024.0f) / 2048.0f)) + 1.0f;
        }

        var aetherytes = Service.DataManager.GetExcelSheet<Aetheryte>();
        var mapMarkers = Service.DataManager.GetSubrowExcelSheet<MapMarker>();

        foreach (var aetheryte in aetherytes) {
            if (!aetheryte.IsAetheryte)
                continue;

            if (aetheryte.Territory.Value.RowId != mapLink.TerritoryType.RowId)
                continue;

            var map = aetheryte.Map.Value;
            var scale = map.SizeFactor;
            var name = map.PlaceName.Value.Name.ExtractText();

            var mapMarker = mapMarkers
	            .SelectMany(markers => markers)
	            .FirstOrDefault(m => m.DataType == 3 && m.DataKey.RowId == aetheryte.RowId);
            
            if (mapMarker.RowId is 0) {
                Service.PluginLog.Debug($"Could not find aetheryte: {name}");
                return 0;
            }

            var aetherX = ConvertRawPositionToMapCoordinate(mapMarker.X, scale);
            var aetherY = ConvertRawPositionToMapCoordinate(mapMarker.Y, scale);

            // var aetheryteName = aetheryte.PlaceName.Value!;
            // Service.PluginLog.Debug($"Aetheryte found: {aetherName} ({aetherX} ,{aetherY})");
            var distance = Math.Pow(aetherX - mapLink.XCoord, 2) + Math.Pow(aetherY - mapLink.YCoord, 2);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestAetheryteId = aetheryte.RowId;
            }
        }
        return closestAetheryteId;
    }
    private unsafe void Teleport(uint aetheryteId) {
        if (Service.ClientState.LocalPlayer == null) return;
        if (Service.Configuration.DisableTeleport) return;

        Telepo.Instance()->Teleport(aetheryteId, 0);
    }
    private unsafe void ReceiveEventDetour(AddonEvent type, AddonArgs args) {
        try {
            if (args is AddonReceiveEventArgs receiveEventArgs && (AtkEventType)receiveEventArgs.AtkEventType is AtkEventType.ButtonClick) {
                this.ReceiveEvent((AddonRelicNoteBook*)receiveEventArgs.Addon, (AtkEvent*)receiveEventArgs.AtkEvent);
            }
        }
        catch (Exception ex) {
            Service.PluginLog.Error(ex, "Exception during hook: AddonRelicNotebook.ReceiveEvent:Click");
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

        var selectedTarget = targetComponent switch
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

        // Service.PluginLog.Debug($"Target selected: {selectedTarget.Name} in {zoneName}.");
        if (Service.Configuration.BraveEchoTarget)
        {
            var sb = new SeStringBuilder()
                .AddText("Target selected: ")
                .AddUiForeground(selectedTarget.Name, 62);

            if (index == 3) // leves
                sb.AddText($" from {selectedTarget.Issuer}");

            sb.AddText($" in {zoneName}.");

            Service.Plugin.PrintMessage(sb.BuiltString);
        }

        if (Service.Configuration.BraveCopyTarget)
        {
            Service.Plugin.PrintMessage($"Copied {selectedTarget.Name} to clipboard.");
            ImGui.SetClipboardText(selectedTarget.Name);

        }
        // Always update internal display, regardless of whether clipboard copy is enabled
        Service.Plugin.TargetWindow?.SetTarget(selectedTarget.Name);

        var aetheryteId = GetNearestAetheryte(selectedTarget.Position);
        if (aetheryteId == 0)
        {
            if (index == 1)
            {
                // Dungeons
                AgentContentsFinder.Instance()->OpenRegularDuty(selectedTarget.ContentsFinderConditionId);
            }
            else
            {
                Service.PluginLog.Warning($"Could not find an aetheryte for {zoneName}");
            }
        }
        else
        {
            Service.GameGui.OpenMapWithMapLink(selectedTarget.Position);
            this.Teleport(aetheryteId);
            if (!Service.Configuration.IsAtmaManagerEnabled)
                return;
            if (!awaitingTeleportFromRelicBookClick)
            {
                awaitingTeleportFromRelicBookClick = true;
                Svc.Framework.Update += WaitForBetweenAreasAndExecute;
            }
            return;
        }
    }
    private void MonitorUnstuck(IFramework _)
    {
        if (!IsPathing || _advancedUnstuck.IsRunning || Player.Object == null)
            return;
        var now = DateTime.Now;
        var currentPos = Player.Object.Position;
        if (Vector3.Distance(_lastPosition, currentPos) >= MinMovementDistance)
        {
            _lastPosition = currentPos;
            _lastMovement = now;
        }
        else if ((now - _lastMovement).TotalSeconds > NavResetThreshold)
        {
            Service.PluginLog.Debug($"AdvancedUnstuck: stuck detected. Moved {Vector3.Distance(_lastPosition, currentPos)} yalms in {(now - _lastMovement).TotalSeconds:F1} seconds.");
            restartNavAfterUnstuck = true;
            _advancedUnstuck.Start();
            _lastMovement = now; // Prevent spamming Start
        }
    }
    internal void WaitForBetweenAreasAndExecute(IFramework framework)
    {
        if (!Service.Configuration.IsAtmaManagerEnabled || !awaitingTeleportFromRelicBookClick)
            return;
        if (!hasEnteredBetweenAreas)
        {
            if (Svc.Condition[ConditionFlag.BetweenAreas])
            {
                hasEnteredBetweenAreas = true;
            }
        }
        else
        {
            if (!Svc.Condition[ConditionFlag.BetweenAreas] && GenericHelpers.IsScreenReady() && !hasQueuedMountTasks)
            {
                hasQueuedMountTasks = true;
                EnqueueMountUp();
            }
        }
    }
    private unsafe void EnqueueMountUp()
    {
        TaskManager.Enqueue(() => NavReady);

        // Don't skip mounting
        TaskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
            {
                Service.PluginLog.Debug("Already mounted, skipping mount roulette use.");
                return true;
            }
            var am = ActionManager.Instance();
            const uint rouletteId = 9;
            if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
            {
                Service.PluginLog.Debug("Attempting to use mount roulette...");
                if (am->UseAction(ActionType.GeneralAction, rouletteId))
                {
                    Service.PluginLog.Debug("Using mount roulette.");
                }
                else
                {
                    Service.PluginLog.Warning("Failed to use mount roulette.");
                }
            }
            else
            {
                Service.PluginLog.Warning("Mount roulette unavailable.");
            }
            return true;
        });
        TaskManager.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Debug("Skipping wait for mounted because AdvancedUnstuck active.");
                return true;
            }
            return Svc.Condition[ConditionFlag.Mounted];
        });
        TaskManager.Enqueue(() =>
        {   
            Chat.ExecuteCommand("/vnav flyflag");
            EnqueueUnmountAfterNav();
            hasEnteredBetweenAreas = false;
            awaitingTeleportFromRelicBookClick = false;
            hasQueuedMountTasks = false;
            return true;
        });
    }
    public unsafe void EnqueueUnmountAfterNav()
    {
        if (monitoringPathing) return;
        monitoringPathing = true;
        unmountStartTime = DateTime.Now;
        Svc.Framework.Update += MonitorPathingAndDismount;
    }
    private unsafe void MonitorPathingAndDismount(IFramework _)
    {
        if (_advancedUnstuck.IsRunning)
            return;
        if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
            return;
        if (!monitoringPathing)
            return;
        monitoringPathing = false;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        if (restartNavAfterUnstuck)
        {
            restartNavAfterUnstuck = false;
            RestartNavigationToTarget();
        }
        else
        {
            EnqueueDismount();
            //Unlock TargetInfoWindow now that navigation is fully complete
            if (Service.Plugin.TargetWindow?.State == TargetingState.AwaitingAtmaPathing)
            {
                Service.PluginLog.Debug("[ZodiacBuddy] Scheduling delayed AtmaManager pathing unlock...");
                TaskManager.Enqueue(() =>
                {
                    // Delay to allow TargetInfoWindow.UpdateCurrentTargetInfo to run before unlocking
                    Task.Delay(250).ContinueWith(_ =>
                    {
                        Service.PluginLog.Debug("[ZodiacBuddy] Unlocking TargetInfoWindow pathing after short delay.");
                        Service.Plugin.TargetWindow.OnAtmaPathingComplete();
                    });
                });
            }
        }
    }
    private void RestartNavigationToTarget()
    {
        VNavmesh.Path.Stop();
        Chat.ExecuteCommand($"/vnavmesh moveflag");
        monitoringPathing = true;
        Svc.Framework.Update += MonitorPathingAndDismount;
    }
    private unsafe void EnqueueDismount()
    {
        if (_advancedUnstuck.IsRunning)
        {
            // Delay dismount until unstuck finishes
            Service.PluginLog.Debug("Skipping dismount because AdvancedUnstuck is active.");
            return;
        }
        var am = ActionManager.Instance();
        TaskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0);
        }, "Dismount");
        TaskManager.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Debug("Skipping Wait for not in flight because AdvancedUnstuck active.");
                return true; // skip wait, let the task complete immediately
            }
            return !Svc.Condition[ConditionFlag.InFlight] && CanAct;
        }, 1000, "Wait for not in flight");
        TaskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0);
        }, "Dismount 2");
        TaskManager.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Debug("Skipping Wait for dismount because AdvancedUnstuck active.");
                return true;
            }
            return !Svc.Condition[ConditionFlag.Mounted] && CanAct;
        }, 1000, "Wait for dismount");
        TaskManager.Enqueue(() =>
        {
            if (!Svc.Condition[ConditionFlag.Mounted])
                TaskManager.DelayNextImmediate(500);
        });
    }
    private void OnUnstuckCompleteHandler()
    {
        Service.PluginLog.Debug("Unstuck finished, restarting navigation.");
        RestartNavigationToTarget();
    }
    private void MoveToWithTracking(Vector3 destination)
    {
        _lastKnownPath = new List<Vector3> { destination };
        VNavmesh.Path.MoveTo(_lastKnownPath, false);
    }
    private void StartUnstuckMonitoring()
    {
        if (!monitoringUnstuck)
        {
            monitoringUnstuck = true;
            //reset _lastPosition
            _lastPosition = Player.Object?.Position ?? Vector3.Zero;
            //AdvancedUnstuck.Check handle _lastMovement timing
            Svc.Framework.Update += MonitorUnstuck;
        }
    }
    private void StopUnstuckMonitoring()
    {
        if (monitoringUnstuck)
        {
            Svc.Framework.Update -= MonitorUnstuck;
            monitoringUnstuck = false;
        }
    }
    static unsafe bool IsOwnerNode(AtkEventTarget* target, AtkComponentCheckBox* checkbox)
            => target == checkbox->AtkComponentButton.OwnerNode;
    }
