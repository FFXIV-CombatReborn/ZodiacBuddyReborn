using ECommons.EzIpcManager;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
    private static ICallGateSubscriber<uint, bool>? _contentHasPath;     // "AutoDuty.ContentHasPath" (territoryId -> hasPath)
    private static ICallGateSubscriber<string, string, object>? _setConfig; // "AutoDuty.SetConfig" (key,value)
    private static ICallGateSubscriber<uint, int, bool, object>? _run;      // "AutoDuty.Run" (territoryId, pathIndex, useBareMode)
    private static ICallGateSubscriber<bool>? _isStopped;                 // "AutoDuty.IsStopped"
    private static ICallGateSubscriber<object>? _stop;                    // "AutoDuty.Stop"

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
            _setConfig = Service.Interface.GetIpcSubscriber<string, string, object>("AutoDuty.SetConfig");
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

    /// <summary>
    /// Configure AutoDuty for Unsynced/Support and start the run.
    /// </summary>
    public static bool StartInstance(uint territoryId, DutyMode dutyMode, bool useBareMode = true)
    {
        if (!Enabled || _setConfig is null || _run is null) return false;

        try
        {
            // Mirror Questionable config:
            // Unsynced: true for UnsyncRegular, false for Support
            _setConfig.InvokeAction("Unsynced", (dutyMode == DutyMode.UnsyncRegular).ToString());

            // dutyModeEnum: "Regular" for UnsyncRegular, "Support" otherwise
            var modeStr = dutyMode == DutyMode.UnsyncRegular ? "Regular" : "Support";
            _setConfig.InvokeAction("dutyModeEnum", modeStr);

            // Path index: 1 (same as Questionable); useBareMode: true by default
            _run.InvokeAction(territoryId, 1, useBareMode);
            return true;
        }
        catch (IpcError)
        {
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
                Debug.Assert(PointOnFloor != null);
            }

            [EzIPC("vnavmesh.Query.Mesh.NearestPoint", applyPrefix: false)]
            internal static readonly Func<Vector3, float, float, Vector3> NearestPoint;

            [EzIPC("vnavmesh.Query.Mesh.PointOnFloor", applyPrefix: false)]
            internal static readonly Func<Vector3, bool, float, Vector3> PointOnFloor;
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
