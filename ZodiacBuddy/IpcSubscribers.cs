using ECommons.EzIpcManager;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
namespace ZodiacBuddy;

internal static class IPCSubscriber
{
    public static bool IsReady(string pluginName)
        => DalamudReflector.TryGetDalamudPlugin(pluginName, out _, false, true);
}

internal static class AutoDutyIpc
{
    private static ICallGateSubscriber<uint, bool>? _contentHasPath;     
    private static ICallGateSubscriber<object, bool>? _pushConfigOverrides;
    private static ICallGateSubscriber<bool>? _popConfigOverrides;
    private static ICallGateSubscriber<uint, int, bool, object>? _run;      
    private static ICallGateSubscriber<bool>? _isStopped;                 
    private static ICallGateSubscriber<object>? _stop;                    

    public static bool Enabled { get; private set; }

    public enum DutyMode
    {
        Support = 1,
        UnsyncRegular = 2,
    }

    public static void Init()
    {
        try
        {
            _contentHasPath = Service.Interface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
            _pushConfigOverrides = Service.Interface.GetIpcSubscriber<object, bool>("AutoDuty.PushConfigOverrides");
            _popConfigOverrides = Service.Interface.GetIpcSubscriber<bool>("AutoDuty.PopConfigOverrides");
            _run = Service.Interface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
            _isStopped = Service.Interface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
            _stop = Service.Interface.GetIpcSubscriber<object>("AutoDuty.Stop");
            Enabled = true;
        }
        catch
        {
            Enabled = false;
        }
    }

    public static bool HasPath(uint territoryId)
    {
        try { return _contentHasPath?.InvokeFunc(territoryId) ?? false; }
        catch (IpcError) { return false; }
    }

    public static bool IsStopped()
    {
        try { return _isStopped?.InvokeFunc() ?? true; }
        catch (IpcError) { return true; }
    }

    public static void Stop()
    {
        try { _stop?.InvokeAction(); } catch (IpcError) { /* swallow */ }
    }

    public static bool StartInstance(uint territoryId, DutyMode dutyMode, bool useBareMode = true)
    {
        if (!Enabled || _pushConfigOverrides is null || _run is null) return false;

        var overridesApplied = false;
        try
        {
            var isUnsynced = dutyMode == DutyMode.UnsyncRegular;
            var configOverrides = new Dictionary<string, string>
            {
                ["Meta.DutyModeEnum"] = isUnsynced ? "Regular" : "Support",
                ["Meta.Unsynced"] = isUnsynced.ToString(),
            };
            overridesApplied = _pushConfigOverrides.InvokeFunc(configOverrides);
            if (!overridesApplied)
                return false;

            _run.InvokeAction(territoryId, 1, useBareMode);
            return true;
        }
        catch (IpcError)
        {
            if (overridesApplied)
                RestoreConfigOverrides();
            return false;
        }
    }

    private static void RestoreConfigOverrides()
    {
        try { _popConfigOverrides?.InvokeFunc(); } catch (IpcError) { }
    }
}

internal enum RotationSolverOperatingMode : byte
{
    Off,
    Auto,
    TargetOnly,
    Manual,
    AutoDuty,
    Henched,
    PvP
}

internal enum RotationSolverTargetingType
{
    Big,
    Small,
    HighHP,
    LowHP,
    HighHPPercent,
    LowHPPercent,
    HighMaxHP,
    LowMaxHP,
    Nearest,
    Farthest,
    PvPHealers,
    PvPTanks,
    PvPDPS,
}

internal enum RotationSolverOtherCommandType : byte
{
    Settings,
    Rotations,
    DutyRotations,
    DoActions,
    ToggleActions,
    NextAction,
    Cycle,
}

internal readonly record struct RotationSolverGrinderSettings(
    string HostileType,
    bool IgnoreNonFateInFate,
    bool ForlornPriority);

internal static class RSRIPC
{
    private static ICallGateSubscriber<RotationSolverOperatingMode, object>? changeOperatingMode;
    private static ICallGateSubscriber<RotationSolverOperatingMode, RotationSolverTargetingType, object>? autodutyChangeOperatingMode;
    private static ICallGateSubscriber<RotationSolverOtherCommandType, string, object>? otherCommand;
    private static ICallGateSubscriber<object>? enableTargetFreelyOverride;
    private static ICallGateSubscriber<object>? disableTargetFreelyOverride;

    internal static bool IsLoaded
        => changeOperatingMode is { HasAction: true };

    internal static bool SupportsGrinderAutoMode
        => autodutyChangeOperatingMode is { HasAction: true }
            && enableTargetFreelyOverride is { HasAction: true }
            && disableTargetFreelyOverride is { HasAction: true };

    internal static void Init()
    {
        changeOperatingMode = Service.Interface.GetIpcSubscriber<RotationSolverOperatingMode, object>("RotationSolverReborn.ChangeOperatingMode");
        autodutyChangeOperatingMode = Service.Interface.GetIpcSubscriber<RotationSolverOperatingMode, RotationSolverTargetingType, object>("RotationSolverReborn.AutodutyChangeOperatingMode");
        otherCommand = Service.Interface.GetIpcSubscriber<RotationSolverOtherCommandType, string, object>("RotationSolverReborn.OtherCommand");
        enableTargetFreelyOverride = Service.Interface.GetIpcSubscriber<object>("RotationSolverReborn.EnableTargetFreelyOverride");
        disableTargetFreelyOverride = Service.Interface.GetIpcSubscriber<object>("RotationSolverReborn.DisableTargetFreelyOverride");
    }

    internal static bool TrySetOperatingMode(RotationSolverOperatingMode mode)
    {
        if (!IsLoaded || changeOperatingMode is not { HasAction: true })
            return false;

        try
        {
            changeOperatingMode.InvokeAction(mode);
            return true;
        }
        catch (IpcError exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn IPC rejected {mode}: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn IPC failed for {mode}: {exception.Message}");
            return false;
        }
    }

    internal static bool TrySetAutoDutyOperatingMode(RotationSolverTargetingType targetingType)
    {
        if (autodutyChangeOperatingMode is not { HasAction: true })
            return false;

        try
        {
            autodutyChangeOperatingMode.InvokeAction(RotationSolverOperatingMode.AutoDuty, targetingType);
            return true;
        }
        catch (IpcError exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn AutoDuty IPC rejected targeting={targetingType}: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn AutoDuty IPC failed for targeting={targetingType}: {exception.Message}");
            return false;
        }
    }

    internal static bool TrySetTargetFreelyOverride(bool enabled)
    {
        var subscriber = enabled ? enableTargetFreelyOverride : disableTargetFreelyOverride;
        if (subscriber is not { HasAction: true })
            return false;

        try
        {
            subscriber.InvokeAction();
            return true;
        }
        catch (IpcError exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn TargetFreely override {(enabled ? "enable" : "disable")} was rejected: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn TargetFreely override {(enabled ? "enable" : "disable")} failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TrySetSetting(string name, string value)
    {
        if (otherCommand is not { HasAction: true })
            return false;

        try
        {
            otherCommand.InvokeAction(RotationSolverOtherCommandType.Settings, $"{name} {value}");
            return true;
        }
        catch (IpcError exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn setting write rejected for {name}={value}: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] RotationSolverReborn setting write failed for {name}={value}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryReadGrinderSettings(out RotationSolverGrinderSettings settings, out string error)
    {
        settings = default;
        error = string.Empty;
        if (!TryGetConfigurationObject(out var configuration, out error))
            return false;

        if (!TryReadProperty(configuration, "HostileType", out var hostileType, out error))
            return false;
        if (!TryReadProperty(configuration, "IgnoreNonFateInFate", out var ignoreNonFateRaw, out error))
            return false;
        if (!bool.TryParse(ignoreNonFateRaw, out var ignoreNonFate))
        {
            error = $"RSR IgnoreNonFateInFate returned unexpected value '{ignoreNonFateRaw}'.";
            return false;
        }

        if (!TryReadProperty(configuration, "ForlornPriority", out var forlornPriorityRaw, out error))
            return false;
        if (!bool.TryParse(forlornPriorityRaw, out var forlornPriority))
        {
            error = $"RSR ForlornPriority returned unexpected value '{forlornPriorityRaw}'.";
            return false;
        }

        settings = new RotationSolverGrinderSettings(hostileType, ignoreNonFate, forlornPriority);
        return true;
    }

    private static bool TryGetConfigurationObject(out object configuration, out string error)
    {
        configuration = null!;
        error = string.Empty;
        if (!TryGetLoadedPluginInstance("RotationSolver", out var plugin, out error))
            return false;

        try
        {
            Type? serviceType = plugin.GetType().Assembly.GetType("RotationSolver.Basic.Service", false);
            if (serviceType == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    serviceType = assembly.GetType("RotationSolver.Basic.Service", false);
                    if (serviceType != null)
                        break;
                }
            }
            if (serviceType == null)
            {
                error = "Could not locate RSR's configuration service type.";
                return false;
            }

            var configProperty = serviceType.GetProperty("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            configuration = configProperty?.GetValue(null)!;
            if (configuration == null)
            {
                error = "Could not read RSR's live configuration object.";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not inspect RSR settings: {exception.Message}";
            return false;
        }
    }

    private static bool TryGetLoadedPluginInstance(string internalName, out object plugin, out string error)
    {
        plugin = null!;
        error = string.Empty;

        try
        {
            var pluginManager = DalamudReflector.GetPluginManager();
            var installedProperty = pluginManager.GetType().GetProperty(
                "InstalledPlugins",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (installedProperty?.GetValue(pluginManager) is not System.Collections.IEnumerable installedPlugins)
            {
                error = "Could not inspect Dalamud's installed plugin list.";
                return false;
            }

            foreach (var installed in installedPlugins)
            {
                if (installed == null)
                    continue;

                var wrapperType = installed.GetType();
                var nameProperty = wrapperType.GetProperty(
                    "InternalName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var candidateName = nameProperty?.GetValue(installed)?.ToString();
                if (!string.Equals(candidateName, internalName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var loadedProperty = wrapperType.GetProperty(
                    "IsLoaded",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadedProperty?.GetValue(installed) is bool isLoaded && !isLoaded)
                {
                    error = $"{internalName} is installed but not loaded.";
                    return false;
                }

                for (Type? type = wrapperType; type != null; type = type.BaseType)
                {
                    var instanceField = type.GetField(
                        "instance",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    var instance = instanceField?.GetValue(installed);
                    if (instance != null)
                    {
                        plugin = instance;
                        return true;
                    }
                }

                error = $"{internalName} is loaded but its plugin instance could not be read.";
                return false;
            }

            error = $"{internalName} is not installed or loaded.";
            return false;
        }
        catch (Exception exception)
        {
            error = $"Could not inspect {internalName} through Dalamud's plugin manager: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadProperty(object configuration, string propertyName, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        try
        {
            var property = configuration.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var raw = property?.GetValue(configuration);
            if (raw == null)
            {
                error = $"Could not read RSR setting '{propertyName}'.";
                return false;
            }

            value = raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception)
        {
            error = $"Could not read RSR setting '{propertyName}': {exception.Message}";
            return false;
        }
    }
}

internal readonly record struct BossModNavigationState(bool IsNavigating, bool MovementActive, Vector3? Destination);

internal static class BossModIPC
{
    private static ICallGateSubscriber<List<string>, bool, List<string>>? configuration;
    private static ICallGateSubscriber<string>? getAiPreset;
    private static ICallGateSubscriber<string, object>? setAiPreset;
    private static ICallGateSubscriber<string>? getActivePreset;
    private static ICallGateSubscriber<string, bool>? setActivePreset;
    private static ICallGateSubscriber<Vector3?>? naviTargetPos;
    private static ICallGateSubscriber<bool>? isNavigating;
    private static ICallGateSubscriber<bool>? isMoving;
    private static ICallGateSubscriber<bool, object>? pauseMovement;
    private static ICallGateSubscriber<Vector3, float, bool, bool>? generateObstacleMap;
    private static ICallGateSubscriber<TaskStatus>? getObstacleMapGenerationStatus;
    private static ICallGateSubscriber<bool>? hasTempObstacleMap;
    private static ICallGateSubscriber<bool>? clearTempObstacleMap;

    internal static bool IsLoaded
        => configuration is { HasFunction: true };

    internal static void Init()
    {
        configuration = Service.Interface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration");
        getAiPreset = Service.Interface.GetIpcSubscriber<string>("BossMod.AI.GetPreset");
        setAiPreset = Service.Interface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset");
        getActivePreset = Service.Interface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        setActivePreset = Service.Interface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        naviTargetPos = Service.Interface.GetIpcSubscriber<Vector3?>("BossMod.AI.NaviTargetPos");
        isNavigating = Service.Interface.GetIpcSubscriber<bool>("BossMod.AI.IsNavigating");
        isMoving = Service.Interface.GetIpcSubscriber<bool>("BossMod.Movement.IsMoving");
        pauseMovement = Service.Interface.GetIpcSubscriber<bool, object>("BossMod.AI.PauseMovement");
        generateObstacleMap = Service.Interface.GetIpcSubscriber<Vector3, float, bool, bool>("BossMod.ObstacleMap.Generate");
        getObstacleMapGenerationStatus = Service.Interface.GetIpcSubscriber<TaskStatus>("BossMod.ObstacleMap.GetGenerationStatus");
        hasTempObstacleMap = Service.Interface.GetIpcSubscriber<bool>("BossMod.ObstacleMap.HasTempMap");
        clearTempObstacleMap = Service.Interface.GetIpcSubscriber<bool>("BossMod.ObstacleMap.ClearTempMap");
    }

    internal static bool TryGetConfiguration(string field, out string value)
    {
        value = string.Empty;
        if (!IsLoaded || configuration is not { HasFunction: true })
            return false;

        try
        {
            var result = configuration.InvokeFunc(["AIConfig", field], false);
            if (result.Count != 1)
                return false;

            value = result[0];
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod configuration read failed for {field}: {exception.Message}");
            return false;
        }
    }

    internal static bool TrySetConfiguration(string field, string value)
    {
        if (!IsLoaded || configuration is not { HasFunction: true })
            return false;

        try
        {
            var result = configuration.InvokeFunc(["AIConfig", field, value], false);
            return result.Count == 0;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod configuration write failed for {field}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryGetAiPreset(out string preset)
    {
        preset = string.Empty;
        if (getAiPreset is not { HasFunction: true })
            return false;

        try
        {
            preset = getAiPreset.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    internal static bool TrySetAiPreset(string preset)
    {
        if (setAiPreset is not { HasAction: true })
            return false;

        try
        {
            setAiPreset.InvokeAction(preset);
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    internal static bool TryGetActivePreset(out string? preset)
    {
        preset = null;
        if (getActivePreset is not { HasFunction: true })
            return false;

        try
        {
            preset = getActivePreset.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    internal static bool TryRestoreActivePreset(string preset)
    {
        if (setActivePreset is not { HasFunction: true })
            return false;

        try
        {
            return setActivePreset.InvokeFunc(preset);
        }
        catch (IpcError)
        {
            return false;
        }
    }

    internal static bool TryGetNavigationIntent(out Vector3 destination, out bool movementActive)
    {
        destination = default;
        movementActive = false;
        if (!TryGetNavigationState(out var state))
            return false;

        movementActive = state.MovementActive;
        if (state.Destination is Vector3 position)
            destination = position;
        return true;
    }

    internal static bool TryGetNavigationState(out BossModNavigationState state)
    {
        state = default;
        if (naviTargetPos is not { HasFunction: true }
            || isNavigating is not { HasFunction: true }
            || isMoving is not { HasFunction: true })
            return false;

        try
        {
            var navigating = isNavigating.InvokeFunc();
            var target = navigating ? naviTargetPos.InvokeFunc() : null;
            var moving = isMoving.InvokeFunc();
            state = new BossModNavigationState(navigating, moving, target);
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod navigation-state read failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TryPauseMovement(bool pause)
    {
        if (pauseMovement is not { HasAction: true })
            return false;

        try
        {
            pauseMovement.InvokeAction(pause);
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod movement pause failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TryGenerateTemporaryObstacleMap(Vector3 center, float radius)
    {
        if (generateObstacleMap is not { HasFunction: true })
            return false;

        try
        {
            return generateObstacleMap.InvokeFunc(center, radius, false);
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod temporary obstacle-map generation failed to start: {exception.Message}");
            return false;
        }
    }

    internal static bool TryGetObstacleMapGenerationStatus(out TaskStatus status)
    {
        status = TaskStatus.RanToCompletion;
        if (getObstacleMapGenerationStatus is not { HasFunction: true })
            return false;

        try
        {
            status = getObstacleMapGenerationStatus.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod obstacle-map generation-status read failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TryHasTempObstacleMap(out bool hasTempMap)
    {
        hasTempMap = false;
        if (hasTempObstacleMap is not { HasFunction: true })
            return false;

        try
        {
            hasTempMap = hasTempObstacleMap.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod temporary obstacle-map state read failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TryClearTempObstacleMap()
    {
        if (clearTempObstacleMap is not { HasFunction: true })
            return false;

        try
        {
            return clearTempObstacleMap.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/IPC] BossMod temporary obstacle-map clear failed: {exception.Message}");
            return false;
        }
    }
}

internal static class VNavmesh
{
    internal static bool Enabled
        => IPCSubscriber.IsReady("vnavmesh");

    internal static class Nav
    {
        static Nav()
        {
            EzIPC.Init(typeof(Nav), "vnavmesh");
            Debug.Assert(IsReady != null);
            Debug.Assert(BuildProgress != null);
            Debug.Assert(Reload != null);
            Debug.Assert(Rebuild != null);
            Debug.Assert(Pathfind != null);
            Debug.Assert(PathfindCancelable != null);
            Debug.Assert(PathfindCancelAll != null);
            Debug.Assert(PathfindInProgress != null);
            Debug.Assert(PathfindNumQueued != null);
            Debug.Assert(IsAutoLoad != null);
            Debug.Assert(SetAutoLoad != null);
        }

        [EzIPC("vnavmesh.Nav.IsReady", applyPrefix: false)]
        internal static readonly Func<bool> IsReady;

        [EzIPC("vnavmesh.Nav.BuildProgress", applyPrefix: false)]
        internal static readonly Func<float> BuildProgress;

        [EzIPC("vnavmesh.Nav.Reload", applyPrefix: false)]
        internal static readonly Func<bool> Reload;

        [EzIPC("vnavmesh.Nav.Rebuild", applyPrefix: false)]
        internal static readonly Func<bool> Rebuild;

        [EzIPC("vnavmesh.Nav.Pathfind", applyPrefix: false)]
        internal static readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind;

        [EzIPC("vnavmesh.Nav.PathfindCancelable", applyPrefix: false)]
        internal static readonly Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> PathfindCancelable;

        [EzIPC("vnavmesh.Nav.PathfindCancelAll", applyPrefix: false)]
        internal static readonly Action PathfindCancelAll;

        [EzIPC("vnavmesh.Nav.PathfindInProgress", applyPrefix: false)]
        internal static readonly Func<bool> PathfindInProgress;

        [EzIPC("vnavmesh.Nav.PathfindNumQueued", applyPrefix: false)]
        internal static readonly Func<int> PathfindNumQueued;

        [EzIPC("vnavmesh.Nav.IsAutoLoad", applyPrefix: false)]
        internal static readonly Func<bool> IsAutoLoad;

        [EzIPC("vnavmesh.Nav.SetAutoLoad", applyPrefix: false)]
        internal static readonly Action<bool> SetAutoLoad;
    }

    internal static class Query
    {
        internal static class Mesh
        {
            static Mesh()
            {
                EzIPC.Init(typeof(Mesh), "vnavmesh");
                Debug.Assert(NearestPoint != null);
                Debug.Assert(NearestPointReachable != null);
                Debug.Assert(PointOnFloor != null);
                Debug.Assert(FlagToPoint != null);
            }

            [EzIPC("vnavmesh.Query.Mesh.NearestPoint", applyPrefix: false)]
            internal static readonly Func<Vector3, float, float, Vector3?> NearestPoint;

            [EzIPC("vnavmesh.Query.Mesh.NearestPointReachable", applyPrefix: false)]
            internal static readonly Func<Vector3, float, float, Vector3?> NearestPointReachable;

            [EzIPC("vnavmesh.Query.Mesh.PointOnFloor", applyPrefix: false)]
            internal static readonly Func<Vector3, bool, float, Vector3?> PointOnFloor;

            [EzIPC("vnavmesh.Query.Mesh.FlagToPoint", applyPrefix: false)]
            internal static readonly Func<Vector3?> FlagToPoint;
        }
    }

    internal static class Path
    {
        static Path()
        {
            EzIPC.Init(typeof(Path), "vnavmesh");
            Debug.Assert(MoveTo != null);
            Debug.Assert(Stop != null);
            Debug.Assert(IsRunning != null);
            Debug.Assert(NumWaypoints != null);
            Debug.Assert(GetMovementAllowed != null);
            Debug.Assert(SetMovementAllowed != null);
            Debug.Assert(GetAlignCamera != null);
            Debug.Assert(SetAlignCamera != null);
            Debug.Assert(GetTolerance != null);
            Debug.Assert(SetTolerance != null);
        }

        [EzIPC("vnavmesh.Path.MoveTo", applyPrefix: false)]
        internal static readonly Action<List<Vector3>, bool> MoveTo;

        [EzIPC("vnavmesh.Path.Stop", applyPrefix: false)]
        internal static readonly Action Stop;

        [EzIPC("vnavmesh.Path.IsRunning", applyPrefix: false)]
        internal static readonly Func<bool> IsRunning;

        [EzIPC("vnavmesh.Path.NumWaypoints", applyPrefix: false)]
        internal static readonly Func<int> NumWaypoints;

        [EzIPC("vnavmesh.Path.GetMovementAllowed", applyPrefix: false)]
        internal static readonly Func<bool> GetMovementAllowed;

        [EzIPC("vnavmesh.Path.SetMovementAllowed", applyPrefix: false)]
        internal static readonly Action<bool> SetMovementAllowed;

        [EzIPC("vnavmesh.Path.GetAlignCamera", applyPrefix: false)]
        internal static readonly Func<bool> GetAlignCamera;

        [EzIPC("vnavmesh.Path.SetAlignCamera", applyPrefix: false)]
        internal static readonly Action<bool> SetAlignCamera;

        [EzIPC("vnavmesh.Path.GetTolerance", applyPrefix: false)]
        internal static readonly Func<float> GetTolerance;

        [EzIPC("vnavmesh.Path.SetTolerance", applyPrefix: false)]
        internal static readonly Action<float> SetTolerance;
    }

    internal static class SimpleMove
    {
        static SimpleMove()
        {
            EzIPC.Init(typeof(SimpleMove), "vnavmesh");
            Debug.Assert(PathfindAndMoveTo != null);
            Debug.Assert(PathfindInProgress != null);
        }

        [EzIPC("vnavmesh.SimpleMove.PathfindAndMoveTo", applyPrefix: false)]
        internal static readonly Func<Vector3, bool, bool> PathfindAndMoveTo;

        [EzIPC("vnavmesh.SimpleMove.PathfindInProgress", applyPrefix: false)]
        internal static readonly Func<bool> PathfindInProgress;
    }

    internal static class Window
    {
        static Window()
        {
            EzIPC.Init(typeof(Window), "vnavmesh");
            Debug.Assert(IsOpen != null);
            Debug.Assert(SetOpen != null);
        }

        [EzIPC("vnavmesh.Window.IsOpen", applyPrefix: false)]
        internal static readonly Func<bool> IsOpen;

        [EzIPC("vnavmesh.Window.SetOpen", applyPrefix: false)]
        internal static readonly Action<bool> SetOpen;
    }

    internal static class DTR
    {
        static DTR()
        {
            EzIPC.Init(typeof(DTR), "vnavmesh");
            Debug.Assert(IsShown != null);
            Debug.Assert(SetShown != null);
        }

        [EzIPC("vnavmesh.DTR.IsShown", applyPrefix: false)]
        internal static readonly Func<bool> IsShown;

        [EzIPC("vnavmesh.DTR.SetShown", applyPrefix: false)]
        internal static readonly Action<bool> SetShown;
    }
}
