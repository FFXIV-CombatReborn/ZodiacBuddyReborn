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
///using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
//using FFXIVClientStructs.FFXIV.Common.Math;
using FFXVec3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ZodiacBuddy.Stages.Atma.Data;
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
    private static System.Numerics.Vector3 ToSys(FFXVec3 v) => new(v.X, v.Y, v.Z);

    private bool monitoringPathing = false;
    private DateTime unmountStartTime;
    private bool monitoringUnstuck = false;
    private bool restartNavAfterUnstuck = false;
    private bool hasQueuedMountTasks = false;
    private bool hasEnteredBetweenAreas = false;
    private bool awaitingTeleportFromRelicBookClick = false;
    private enum PathingContext { None, Enemy, Fate, Leve }
    private PathingContext _pathingContext = PathingContext.None;
    private enum UnstuckPhase { Idle, AwaitingPathStart, AwaitingFirstMovement, Active }
    private UnstuckPhase _unstuckPhase = UnstuckPhase.Idle;
    private Vector3 _armPos;
    private PathingContext _resumeContext = PathingContext.None;
    public Vector3? CurrentTargetPosition { get; private set; }
    public bool IsPathGenerating => VNavmesh.Nav.PathfindInProgress();
    public bool IsPathing => VNavmesh.Path.IsRunning();
    public bool NavReady => VNavmesh.Nav.IsReady();
    private readonly TaskManager TaskManager = new();
    private ushort? _pendingFateId;
    public bool CanAct
    {
        get
        {
            var playerObject = Player.Object;
            if (playerObject == null || playerObject.IsDead || Player.IsAnimationLocked)
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
                || (c[85] && !c[ConditionFlag.Gathering]))
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
       // Svc.Framework.Update += MonitorUnstuck;
    }
    /// <inheritdoc/>
    public void Dispose() {
        Svc.Framework.Update -= MonitorUnstuck;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        Svc.Framework.Update -= _advancedUnstuck.RunningUpdate;
        Service.AddonLifecycle.UnregisterListener(ReceiveEventDetour);
        _advancedUnstuck.OnUnstuckComplete -= OnUnstuckCompleteHandler;
        _advancedUnstuck.Dispose();
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
    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        // Turn SeString-ish residuals into plain text expectations
        s = s.Normalize(NormalizationForm.FormKC)
             .Replace('’', '\'')
             .Replace('“', '"').Replace('”', '"')
             .Replace('…', '.')
             .Replace('–', '-') // en dash
             .Replace('—', '-') // em dash
             .Replace('\u00A0', ' '); // NBSP -> space

        // collapse whitespace & trim
        var sb = new StringBuilder(s.Length);
        bool wasSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!wasSpace) { sb.Append(' '); wasSpace = true; }
            }
            else
            {
                sb.Append(ch);
                wasSpace = false;
            }
        }

        return sb.ToString().Trim().ToLowerInvariant();
    }
    private void ResetRunStateForNewCycle()
    {
        Svc.Framework.Update -= MonitorPathingAndDismount;
        Svc.Framework.Update -= MonitorUnstuck;

        monitoringPathing = false;
        monitoringUnstuck = false;

        _unstuckPhase = UnstuckPhase.Idle;
        restartNavAfterUnstuck = false;

        hasEnteredBetweenAreas = false;
        hasQueuedMountTasks = false;
        awaitingTeleportFromRelicBookClick = false;
    }
    private void ResetTeleportCycleFlags()
    {
        hasEnteredBetweenAreas = false;
        hasQueuedMountTasks = false;
        awaitingTeleportFromRelicBookClick = false;
    }
    private static readonly Dictionary<string, ushort> FateNameToId = new()
    {
        ["surprise"] = 317,
        ["heroes of the 2nd"] = 424,
        ["return to cinder"] = 430,
        ["bellyful"] = 475,
        ["giant seps"] = 480,
        ["tower of power"] = 486,
        ["the taste of fear"] = 493,
        ["the four winds"] = 499,
        ["black and nburu"] = 516,
        ["good to be bud"] = 517,
        ["another notch on the torch"] = 521,
        ["quartz coupling"] = 540,
        ["the big bagoly theory"] = 543,
        ["taken"] = 552,
        ["breaching north tidegate"] = 569,
        ["breaching south tidegate"] = 571,
        ["the king's justice"] = 577,
        ["schism"] = 587,
        ["make it rain"] = 589,
        ["in spite of it all"] = 604,
        ["the enmity of my enemy"] = 611,
        ["breaking dawn"] = 616,
        ["everything's better"] = 620,
        ["what gored before"] = 628,
        ["rude awakening"] = 632,
        ["air supply"] = 633,
        ["the ceruleum road"] = 642,
    };
    private unsafe void Teleport(uint aetheryteId) {
        if (Player.Object == null) return;
        if (Service.Configuration.DisableTeleport) return;

        Telepo.Instance()->Teleport(aetheryteId, 0);
    }
    private unsafe void ReceiveEventDetour(AddonEvent type, AddonArgs args) {
        try {
            if (args is AddonReceiveEventArgs receiveEventArgs && (AtkEventType)receiveEventArgs.AtkEventType is AtkEventType.ButtonClick) {
                this.ReceiveEvent((AddonRelicNoteBook*)receiveEventArgs.Addon.Address, (AtkEvent*)receiveEventArgs.AtkEvent);
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
        if (index == 0)
        {
            ResetRunStateForNewCycle();
            _pendingFateId = null;
            Service.Plugin.TargetWindow?.SetTarget(selectedTarget.Name);

            var aetheryteId = GetNearestAetheryte(selectedTarget.Position);
            if (aetheryteId == 0)
            {
                Service.PluginLog.Warning($"Could not find an aetheryte for {zoneName}");
                return;
            }
            Service.GameGui.OpenMapWithMapLink(selectedTarget.Position);
            this.Teleport(aetheryteId);
            if (!Service.Configuration.IsAtmaManagerEnabled)
                return;
            _pathingContext = PathingContext.Enemy;
            ResetTeleportCycleFlags();
            if (!awaitingTeleportFromRelicBookClick)
            {
                awaitingTeleportFromRelicBookClick = true;
                Svc.Framework.Update += WaitForBetweenAreasAndExecute; 
            }
            return;
        }
        else if (index == 1)
        {
            ResetRunStateForNewCycle();
            _pendingFateId = null;
            var cfcId = selectedTarget.ContentsFinderConditionId;

            var territoryId = selectedTarget.Position.TerritoryType.RowId;

            var started = false;
            if (AutoDutyIpc.Enabled)
            {
                if (AutoDutyIpc.HasPath(territoryId))
                {
                    started = AutoDutyIpc.StartInstance(territoryId, AutoDutyIpc.DutyMode.UnsyncRegular, useBareMode: true);
                }
                else
                {
                    Service.PluginLog.Warning($"AutoDuty reports no path for territory {territoryId} ({zoneName}).");
                }
            }

            if (started)
            {
                Service.Plugin.PrintMessage($"AutoDuty: starting unsynced for {selectedTarget.Name}.");
                return;
            }

            AgentContentsFinder.Instance()->OpenRegularDuty(cfcId);
            Service.Plugin.PrintMessage($"AutoDuty unavailable. Opened Duty Finder for {selectedTarget.Name}.");
            return;
        }

        else if (index == 2)
        {
            ResetRunStateForNewCycle();
            _pathingContext = PathingContext.Fate;

            hasEnteredBetweenAreas = false;
            hasQueuedMountTasks = false;
            var norm = Normalize(selectedTarget.Name);
            if (!FateNameToId.TryGetValue(norm, out var fateId))
            {
                Service.PluginLog.Warning($"[ZBR] Unknown FATE name '{selectedTarget.Name}' - cannot resolve FateId.");
                _pendingFateId = null;
            }
            else
            {
                _pendingFateId = fateId;
                Service.PluginLog.Debug($"[ZBR] Pending FateId set to {_pendingFateId.Value} for '{selectedTarget.Name}'.");
            }

            var fatePos = selectedTarget.Position;
            Service.GameGui.OpenMapWithMapLink(fatePos);

            var aetheryteId = GetNearestAetheryte(fatePos);
            if (aetheryteId == 0)
            {
                Service.PluginLog.Warning("[ZBR] No aetheryte found for selected FATE zone.");
                return;
            }

            this.Teleport(aetheryteId);
            if (!awaitingTeleportFromRelicBookClick)
            {
                awaitingTeleportFromRelicBookClick = true;
                Svc.Framework.Update += WaitForBetweenAreasAndExecute;
            }
            return;
        }
        else if (index == 3)
        {
            ResetRunStateForNewCycle();
            _pendingFateId = null;
            var aetheryteId = GetNearestAetheryte(selectedTarget.Position);
            if (aetheryteId == 0)
            {
                Service.PluginLog.Warning($"Could not find an aetheryte for {zoneName}");
                Service.GameGui.OpenMapWithMapLink(selectedTarget.Position);
                return;
            }
            Service.GameGui.OpenMapWithMapLink(selectedTarget.Position);
            this.Teleport(aetheryteId);

            _pathingContext = PathingContext.Leve;
            ResetTeleportCycleFlags();
            if (!awaitingTeleportFromRelicBookClick)
            {
                awaitingTeleportFromRelicBookClick = true;
                Svc.Framework.Update += WaitForBetweenAreasAndExecute; 
            }
            return;
        }
    }
    private static bool TryGetLiveFateById(ushort fateId, out IFate fate)
    {
        foreach (var f in Svc.Fates)
        {
            if (f.FateId == fateId)
            {
                if (f.State == FateState.Preparing) break;
                fate = f;
                return true;
            }
        }
        fate = default!;
        return false;
    }
    private void MonitorUnstuck(IFramework _)
    {
        if (Player.Object == null) return;
        switch (_unstuckPhase)
        {
            case UnstuckPhase.Idle:
                return;

            case UnstuckPhase.AwaitingPathStart:
                if (!VNavmesh.Nav.PathfindInProgress()    
                    && VNavmesh.Path.IsRunning()           
                    && VNavmesh.Path.NumWaypoints() > 0)  
                {
                    _armPos = Player.Object.Position;
                    _unstuckPhase = UnstuckPhase.AwaitingFirstMovement;
                }
                return;

            case UnstuckPhase.AwaitingFirstMovement:
                if (Vector3.Distance(_armPos, Player.Object.Position) >= MinMovementDistance)
                {
                    _lastPosition = Player.Object.Position;
                    _lastMovement = DateTime.Now; 
                    _unstuckPhase = UnstuckPhase.Active;
                }
                return;

            case UnstuckPhase.Active:
                break; 
        }
        if (!IsPathing || _advancedUnstuck.IsRunning) return;

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
            _resumeContext = _pathingContext;
            restartNavAfterUnstuck = true;
            _advancedUnstuck.Start();
            _lastMovement = now;
        }
    }

    internal void WaitForBetweenAreasAndExecute(IFramework framework)
    {
        if (!Service.Configuration.IsAtmaManagerEnabled || !awaitingTeleportFromRelicBookClick)
            return;

        if (!hasEnteredBetweenAreas)
        {
            if (Svc.Condition[ConditionFlag.BetweenAreas])
                hasEnteredBetweenAreas = true;
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
        if (!GenericHelpers.IsScreenReady()) return;
        if (hasQueuedMountTasks) return;

        hasQueuedMountTasks = true;

        if (_pathingContext == PathingContext.Fate)
        {
            if (_pendingFateId is ushort wantId)
            {
                Service.PluginLog.Debug($"[ZBR] Post-teleport check for FateId={wantId} (queued={hasQueuedMountTasks}).");

                if (TryGetLiveFateById(wantId, out var liveFate))
                {
                    Service.PluginLog.Debug($"[ZBR] FateId={wantId} is present and active. Moving.");
                    EnqueueMountAndFlyTo(liveFate.Position);

                    hasQueuedMountTasks = true;
                }
                else
                {
                    Service.PluginLog.Debug("[ZBR] Clicked FATE id not present/active. Holding at aetheryte.");
                    hasQueuedMountTasks = true; 
                }
            }
            else
            {
                Service.PluginLog.Warning("[ZBR] No pending FATE id set. Holding at aetheryte.");
                hasQueuedMountTasks = true;
            }

            awaitingTeleportFromRelicBookClick = false;
            Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
            return;
        }
        else
        {
            EnqueueMountUp();
        }
        awaitingTeleportFromRelicBookClick = false;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
    }
    private unsafe void EnqueueMountAndFlyTo(System.Numerics.Vector3 destination)
    {
        TaskManager.Enqueue(() => NavReady);
        // Mount (skip if already mounted)
        TaskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted]) return true;
            var am = ActionManager.Instance();
            const uint rouletteId = 9;
            if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
                am->UseAction(ActionType.GeneralAction, rouletteId);
            return true;
        });
        TaskManager.Enqueue(() => _advancedUnstuck.IsRunning || Svc.Condition[ConditionFlag.Mounted]);

        TaskManager.Enqueue(() =>
        {
            VNavmesh.SimpleMove.PathfindAndMoveTo(destination, true);
            EnqueueUnmountAfterNav();
            return true;
        });
    }
    private unsafe void EnqueueMountUp()
    {
        TaskManager.Enqueue(() => NavReady);

        // Dont skip mounting
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
        _unstuckPhase = UnstuckPhase.AwaitingPathStart;
        StartUnstuckMonitoring();

        Svc.Framework.Update -= MonitorPathingAndDismount;
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
                if (_pathingContext == PathingContext.Enemy)
                {
                    Service.Plugin.TargetWindow.OnAtmaPathingComplete();
                    TaskManager.Enqueue(() => { TaskManager.DelayNextImmediate(750); return true; });
                    TaskManager.Enqueue(() =>
                    {
                        if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
                            return true;

                        var tWin = Service.Plugin.TargetWindow;
                        var posOpt = tWin?.CurrentTargetPosition;
                        if (posOpt is FFXVec3 ffxPos)
                        {
                            VNavmesh.SimpleMove.PathfindAndMoveTo(ToSys(ffxPos), false);
                        }
                        //else
                        //{
                            // Optional FALLBACK(commed out for if i need it later)
                            //Chat.ExecuteCommand("/vnav moveflag");
                        //}
                        return true;
                    });

                }

                else if (_pathingContext == PathingContext.Fate)
                {
                    hasEnteredBetweenAreas = false;
                    awaitingTeleportFromRelicBookClick = false;
                    hasQueuedMountTasks = false;
                }
                _pathingContext = PathingContext.None;
                _unstuckPhase = UnstuckPhase.Idle;
                StopUnstuckMonitoring();
                Svc.Framework.Update -= MonitorPathingAndDismount;
                return true;
            });
        }
    }
    private void RestartNavigationToTarget()
    {
        VNavmesh.Path.Stop();

        if (_resumeContext != PathingContext.None)
            _pathingContext = _resumeContext;

        switch (_pathingContext)
        {
            case PathingContext.Enemy:
                {
                    var tWin = Service.Plugin.TargetWindow;
                    var posOpt = tWin?.CurrentTargetPosition;

                    if (posOpt is FFXVec3 ffxPos)
                    {
                        var sysPos = ToSys(ffxPos);
                        Service.PluginLog.Debug($"[ZodiacBuddy] Restart nav (Enemy): nudging toward TargetWindow pos {sysPos}.");
                        VNavmesh.SimpleMove.PathfindAndMoveTo(sysPos, false);
                    }
                    else
                    {
                        Service.PluginLog.Debug("[ZodiacBuddy] Restart nav (Enemy): no TargetWindow pos; using /vnav flyflag.");
                        Chat.ExecuteCommand("/vnav moveflag");
                    }
                    break;
                }

            case PathingContext.Fate:
                if (_pendingFateId is ushort wantId && TryGetLiveFateById(wantId, out var liveFate))
                {
                    VNavmesh.SimpleMove.PathfindAndMoveTo(liveFate.Position, true);
                }
                break;

            case PathingContext.Leve:
                Chat.ExecuteCommand("/vnav flyflag");
                break;

            default:
                Chat.ExecuteCommand("/vnavmesh moveflag");
                break;
        }
        _unstuckPhase = UnstuckPhase.AwaitingPathStart;
        StartUnstuckMonitoring();

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
    private void StartUnstuckMonitoring()
    {
        if (!monitoringUnstuck)
        {
            monitoringUnstuck = true;
            _lastPosition = Player.Object?.Position ?? Vector3.Zero;
            _lastMovement = DateTime.Now;
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
