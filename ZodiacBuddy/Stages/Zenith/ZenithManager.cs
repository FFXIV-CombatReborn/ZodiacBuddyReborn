using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using ZodiacBuddy.Stages.Atma.Movement;
using ZodiacBuddy.Stages.Atma.Unstuck;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Your buddy for the Zodiac Zenith stage.
/// </summary>
public class ZenithManager : IDisposable {
    private readonly ZenithWindow window;
    private Vector3 _lastPosition;
    private DateTime _lastMovement = DateTime.Now;
    private const float MinMovementDistance = 0.2f;
    private const float NavResetThreshold = 3f;
    private readonly AdvancedUnstuck _advancedUnstuck;
    private bool monitoringPathing = false;
    private DateTime unmountStartTime;
    private bool restartNavAfterUnstuck = false;
    private bool hasQueuedMountTasks = false;
    private bool hasEnteredBetweenAreas = false;
    private bool awaitingTeleportFromZenithClick = false;
    private List<Vector3>? _lastKnownPath;
    private readonly TaskManager TaskManager = new();

    /// <summary>
    /// Item ID for Allagan Tomestone of Poetics (commonly used tomestone in 2025)
    /// </summary>
    private const uint TomestoneOfPoeticsId = 28;

    /// <summary>
    /// Item ID for Thavnairian Mist (used for Zenith upgrades)
    /// </summary>
    private const uint ThavnairianMistId = 6268;

    public bool IsPathGenerating => VNavmesh.Nav.PathfindInProgress();
    public bool IsPathing => VNavmesh.Path.IsRunning();
    public bool NavReady => VNavmesh.Nav.IsReady();

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

    /// <summary>
    /// Gets the current count of Allagan Tomestones of Poetics
    /// </summary>
    public static unsafe int GetTomestoneCount() {
        return (int)InventoryManager.Instance()->GetInventoryItemCount(TomestoneOfPoeticsId);
    }

    /// <summary>
    /// Gets the current count of Thavnairian Mist
    /// </summary>
    public static unsafe int GetThavnairianMistCount() {
        return (int)InventoryManager.Instance()->GetInventoryItemCount(ThavnairianMistId);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZenithManager"/> class.
    /// </summary>
    public ZenithManager() {
        this.window = new ZenithWindow();
        this.window.Manager = this; // Wire up reference for navigation
        _advancedUnstuck = new AdvancedUnstuck();
        _advancedUnstuck.OnUnstuckComplete += OnUnstuckCompleteHandler;
        
        Service.Framework.Update += this.OnUpdate;
        Service.Interface.UiBuilder.Draw += this.window.Draw;
        Svc.Framework.Update += _advancedUnstuck.RunningUpdate;
        Svc.Framework.Update += MonitorUnstuck;
    }

    private static ZenithConfiguration Configuration => Service.Configuration.Zenith;

    /// <inheritdoc/>
    public void Dispose() {
        Service.Framework.Update -= this.OnUpdate;
        Service.Interface.UiBuilder.Draw -= this.window.Draw;
        Svc.Framework.Update -= MonitorUnstuck;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        Svc.Framework.Update -= _advancedUnstuck.RunningUpdate;
    }

    private void OnUpdate(IFramework framework) {
        try {
            if (!Configuration.DisplayRelicInfo) {
                this.window.ShowWindow = false;
                return;
            }

            var mainhand = Util.GetEquippedItem(0);
            var offhand = Util.GetEquippedItem(1);

            var shouldShowWindow =
                ZenithRelic.Items.ContainsKey(mainhand.ItemId) ||
                ZenithRelic.Items.ContainsKey(offhand.ItemId);

            this.window.ShowWindow = shouldShowWindow;
            this.window.MainHandItem = mainhand;
            this.window.OffhandItem = offhand;
        }
        catch (Exception ex) {
            Service.PluginLog.Error(ex, $"Unhandled error during {nameof(ZenithManager)}.{nameof(this.OnUpdate)}");
        }
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

    public void NavigateToLocation(MapLinkPayload location) {
        if (!EzThrottler.Throttle("ZenithNavigate", 500))
            return;

        var aetheryteId = GetNearestAetheryte(location);
        if (aetheryteId == 0) {
            Service.PluginLog.Warning($"Could not find an aetheryte for location");
            return;
        }

        Service.GameGui.OpenMapWithMapLink(location);
        this.Teleport(aetheryteId);
        
        if (!awaitingTeleportFromZenithClick) {
            awaitingTeleportFromZenithClick = true;
            Svc.Framework.Update += WaitForBetweenAreasAndExecute;
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
            _lastMovement = now;
        }
    }

    internal void WaitForBetweenAreasAndExecute(IFramework framework)
    {
        if (!awaitingTeleportFromZenithClick)
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
            awaitingTeleportFromZenithClick = false;
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

            TaskManager.Enqueue(() =>
            {
                if (Svc.Condition[ConditionFlag.Mounted])
                {
                    Service.PluginLog.Debug("[ZodiacBuddy] Player still mounted after dismount tasks. Waiting another tick.");
                    return false;
                }

                if (VNavmesh.Path.IsRunning())
                {
                    Service.PluginLog.Debug("[ZodiacBuddy] Navmesh is still running after dismount tasks. Waiting another tick.");
                    return false;
                }

                Service.PluginLog.Debug("[ZodiacBuddy] Player dismounted and navmesh idle. Unlocking pathing.");
                return true;
            });
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
                return true;
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

    public void StopNavigation()
    {
        Service.PluginLog.Debug("Stopping Zenith navigation...");
        
        // Stop VNavmesh pathfinding
        VNavmesh.Path.Stop();
        
        // Clear task queue
        TaskManager.Abort();
        
        // Reset states
        awaitingTeleportFromZenithClick = false;
        hasEnteredBetweenAreas = false;
        hasQueuedMountTasks = false;
        monitoringPathing = false;
        restartNavAfterUnstuck = false;
        
        // Unregister event handlers
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        
        // Stop unstuck if running
        if (_advancedUnstuck.IsRunning)
        {
            _advancedUnstuck.Dispose();
        }
        
        Service.PluginLog.Debug("Zenith navigation stopped.");
    }
}