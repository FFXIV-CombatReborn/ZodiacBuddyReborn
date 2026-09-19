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

internal partial class AnimusManager : IDisposable {
    
    private Vector3 _lastPosition;
    private DateTime _lastMovement = DateTime.Now;
    private const float MinMovementDistance = 0.2f;
    private const float NavResetThresholdSeconds = 3f;
    private const int MaxUnstuckAttempts = 2;
    private const float EnemyAreaMountDistance = 30f;
    private const float EnemyTargetMountDistance = 35f;
    private const float EnemyMountedFlightDistance = 20f;
    private const int EnemyDismountRecoveryMs = 3000;
    private const int EnemyLandingRecoveryMaxAttempts = 4;
    private const float EnemyLandingRecoveryRadius = 10f;
    private const int EnemyTargetFollowFlightRecoveryMaxAttempts = 4;
    private const float EnemyTargetFollowFlightRecoveryRadius = 10f;
    private const int EnemyCompletionCombatSettleMs = 2000;
    private const float GroundGuidedFlightClearance = 2f;
    private readonly AdvancedUnstuck _advancedUnstuck;
    private readonly MobSpawnResolver _mobSpawnResolver;
    private readonly EnemyAutomationController _enemyAutomation;
    private readonly List<Vector3> _visitedEnemySpawnDestinations = [];
    private Vector3? _lastEnemySpawnDestination;
    private MapLinkPayload? _preferredInitialEnemyMapLink;
    private DateTime _enemyDismountRequestedAt = DateTime.MinValue;
    private Vector3? _enemyLandingRecoveryAnchor;
    private int _enemyLandingRecoveryAttempts;
    private int _enemyTargetFollowFlightRecoveryAttempts;
    private ulong _enemyTargetFollowRecoveryTargetId;
    private bool _enemyTargetFollowRecoveryActive;
    private bool _enemyAutomationFailed;
    private bool _ughamaroMineGroundTravelActive;
    private bool _ughamaroMineGroundHandoffAttempted;
    private bool _ughamaroMineGroundHandoffDeferredLogged;

    private static System.Numerics.Vector3 ToSys(FFXVec3 v) => new(v.X, v.Y, v.Z);

    private bool _monitoringUnstuck = false;
    private bool _mountTasksQueued = false;
    private bool _enteredBetweenAreas = false;
    private bool _teleportWaiterActive = false;
    private bool _enemySpawnRelocationQueued = false;
    private bool _enemyCompletionReleasePrepared = false;
    private DateTime _enemyCompletionCombatClearSince = DateTime.MinValue;
    private enum PathingContext { None, Enemy, Fate, Leve }
    private enum NavigationPurpose { InitialEnemyMapFlagTravel, EnemySpawnRelocation, EnemyLandingRecovery, EnemyTargetFollowRecovery, EnemyAggroCleanup, TargetFollow, FateTravel, UGhamaroMineGroundHandoff, LeveMapFlagTravel }
    private enum NavigationRestartPolicy { PreserveFlight, WalkAfterUnstuck }
    private enum EnemyMountedTravelMode { Ground, Flight }
    private sealed class NavigationRestartDescriptor
    {
        public NavigationRestartDescriptor(long runId, Vector3 destination, Func<Vector3?>? destinationResolver, bool fly, Action<AnimusNavigationResult> completed, NavigationPurpose purpose, NavigationRestartPolicy restartPolicy, Action<NavigationRestartDescriptor, AnimusNavigationResult>? terminalResultHandler, int unstuckAttempts, bool groundGuided = false, float arrivalDistance = 3f)
        {
            RunId = runId;
            Destination = destination;
            DestinationResolver = destinationResolver;
            Fly = fly;
            Completed = completed;
            Purpose = purpose;
            RestartPolicy = restartPolicy;
            TerminalResultHandler = terminalResultHandler;
            UnstuckAttempts = unstuckAttempts;
            GroundGuided = groundGuided;
            ArrivalDistance = arrivalDistance;
        }

        public long RunId { get; }
        public Vector3 Destination { get; }
        public Func<Vector3?>? DestinationResolver { get; }
        public bool Fly { get; }
        public Action<AnimusNavigationResult> Completed { get; }
        public NavigationPurpose Purpose { get; }
        public NavigationRestartPolicy RestartPolicy { get; }
        public Action<NavigationRestartDescriptor, AnimusNavigationResult>? TerminalResultHandler { get; }
        public int UnstuckAttempts { get; }
        public bool GroundGuided { get; }
        public float ArrivalDistance { get; }
        public bool UGhamaroEgressArmed { get; set; }

        public NavigationRestartDescriptor WithUnstuckAttempt()
        {
            var next = new NavigationRestartDescriptor(RunId, Destination, DestinationResolver, Fly, Completed, Purpose, RestartPolicy, TerminalResultHandler, UnstuckAttempts + 1, GroundGuided, ArrivalDistance)
            {
                UGhamaroEgressArmed = this.UGhamaroEgressArmed,
            };
            return next;
        }
    }
    private PathingContext _pathingContext = PathingContext.None;
    private enum UnstuckPhase { Idle, AwaitingPathStart, AwaitingFirstMovement, Active }
    private UnstuckPhase _unstuckPhase = UnstuckPhase.Idle;
    private Vector3 _armPos;
    private NavigationRestartDescriptor? _restartNavigation;
    private EnemyObjectiveDefinition? _activeEnemyTarget;
    public bool IsPathing => VNavmesh.Path.IsRunning();
    private readonly AnimusAutomationRun _automationRun = new();
    private long _teleportWaitRunId;
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
    public AnimusManager() 
    {
        _mobSpawnResolver = new MobSpawnResolver();
        _advancedUnstuck = new AdvancedUnstuck();
        _enemyAutomation = new EnemyAutomationController(this);
        _advancedUnstuck.Completed += OnUnstuckCompleteHandler;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", ReceiveEventDetour);
    }
    public void Dispose() {
        Svc.Framework.Update -= TickBookAutomation;
        Svc.Framework.Update -= TickFateGrinding;
        CancelActiveRun();
        Svc.Framework.Update -= MonitorUnstuck;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Service.AddonLifecycle.UnregisterListener(ReceiveEventDetour);
        _advancedUnstuck.Completed -= OnUnstuckCompleteHandler;
        _enemyAutomation.Dispose();
        _advancedUnstuck.Dispose();
        _automationRun.Dispose();
    }
    private void ResetRunStateForNewCycle()
    {
        CancelActiveRun();
    }
    private long StartAutomationRun()
        => _automationRun.Start();
    private void CancelActiveRun()
    {
        StopFateAutomationState(true);
        StopLeveAutomationState(true);
        StopDungeonAutomationState(true);
        _advancedUnstuck.Cancel();
        _automationRun.Cancel();
        _restartNavigation = null;
        _activeEnemyTarget = null;
        _lastEnemySpawnDestination = null;
        _preferredInitialEnemyMapLink = null;
        _enemyDismountRequestedAt = DateTime.MinValue;
        _enemyLandingRecoveryAnchor = null;
        _enemyLandingRecoveryAttempts = 0;
        _enemyTargetFollowFlightRecoveryAttempts = 0;
        _enemyTargetFollowRecoveryTargetId = 0;
        _enemyTargetFollowRecoveryActive = false;
        _enemyAutomationFailed = false;
        _ughamaroMineGroundHandoffAttempted = false;
        _ughamaroMineGroundHandoffDeferredLogged = false;
        _visitedEnemySpawnDestinations.Clear();
        _enemyAutomation.CancelAutomationNavigation();
        StopTeleportWaiter();
        StopUnstuckMonitoring();
        _unstuckPhase = UnstuckPhase.Idle;
        _pathingContext = PathingContext.None;
        _enteredBetweenAreas = false;
        _mountTasksQueued = false;
        _enemySpawnRelocationQueued = false;
        _enemyCompletionReleasePrepared = false;
        _enemyCompletionCombatClearSince = DateTime.MinValue;
    }
    internal EnemyAutomationSnapshot GetEnemyAutomationSnapshot()
        => _enemyAutomation.GetSnapshot();

    internal bool HasActiveAutomationRun => _automationRun.HasActiveRun;
    internal bool IsEnemySpawnRelocationInProgress
        => _enemySpawnRelocationQueued || (_restartNavigation?.Purpose == NavigationPurpose.EnemySpawnRelocation && _automationRun.HasActiveRun);

}
