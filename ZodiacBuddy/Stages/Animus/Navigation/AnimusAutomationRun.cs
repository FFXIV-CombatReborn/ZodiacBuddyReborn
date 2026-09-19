using Dalamud.Plugin.Services;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace ZodiacBuddy.Stages.Animus;

internal enum AnimusAutomationRunState
{
    Idle,
    Active,
    AwaitingNavmeshReadiness,
    AwaitingNavigationStart,
    GeneratingPath,
    FollowingPath,
    Arrived,
    Cancelled,
    Failed
}

internal enum AnimusNavigationResult
{
    Rejected,
    NavmeshUnavailable,
    NavmeshReadinessTimedOut,
    StartupTimedOut,
    StoppedBeforeArrival,
    Arrived,
    Cancelled
}

internal sealed class AnimusAutomationRun : IDisposable
{
    private const double NavmeshReadinessGraceSeconds = 30;
    private const double NavmeshBuildStallSeconds = 120;
    private const double NavmeshReadinessAbsoluteSeconds = 900;
    private const double NavmeshProgressLogSeconds = 15;
    private const double NavigationStartTimeoutSeconds = 10;
    private const float ArrivalDistance = 3f;
    private TaskManager _taskManager = CreateTaskManager();
    private long _nextRunId;
    private long _activeRunId;
    private long _readinessRunId;
    private DateTime _readinessGraceDeadline;
    private DateTime _readinessStallDeadline;
    private DateTime _readinessAbsoluteDeadline;
    private DateTime _readinessNextLogTime;
    private float _readinessProgress = float.NaN;
    private Action? _readinessCompleted;
    private Action<AnimusNavigationResult>? _readinessFailed;
    private long _navigationRunId;
    private Vector3 _navigationDestination;
    private DateTime _navigationStartDeadline;
    private Action<AnimusNavigationResult>? _navigationCompleted;
    private Func<bool>? _navigationPaused;
    private Task<List<Vector3>>? _navigationPathfindTask;
    private CancellationTokenSource? _navigationPathfindCancellation;
    private bool _navigationFly;
    private float _navigationArrivalDistance = ArrivalDistance;
    private Task<List<Vector3>>? _guidedPathfindTask;
    private CancellationTokenSource? _guidedPathfindCancellation;
    private float _guidedFlightClearance;
    private bool _navigationOwned;
    private bool _disposed;

    private static TaskManager CreateTaskManager()
        => new()
        {
            ShowDebug = false,
            TimeoutSilently = true,
        };

    public long ActiveRunId => _activeRunId;
    public bool HasActiveRun => _activeRunId != 0;
    public AnimusAutomationRunState State { get; private set; } = AnimusAutomationRunState.Idle;

    public long Start()
    {
        ThrowIfDisposed();
        Cancel();
        _activeRunId = ++_nextRunId;
        State = AnimusAutomationRunState.Active;
        return _activeRunId;
    }

    public bool IsActive(long runId)
        => runId != 0 && runId == _activeRunId;

    public void Enqueue(Func<bool?> step)
        => Enqueue(step, string.Empty);

    public void Enqueue(Action step)
        => Enqueue(step, string.Empty);

    public void Enqueue(Action step, string name)
    {
        var runId = _activeRunId;
        if (runId == 0)
            return;

        _taskManager.Enqueue(() =>
        {
            if (IsActive(runId))
                step();
        }, name);
    }

    public void Enqueue(Func<bool?> step, string name)
    {
        var runId = _activeRunId;
        if (runId == 0)
            return;

        _taskManager.Enqueue(() => IsActive(runId) ? step() : true, name);
    }

    public void Enqueue(Func<bool?> step, int timeout, string name)
    {
        var runId = _activeRunId;
        if (runId == 0)
            return;

        _taskManager.Enqueue(() => IsActive(runId) ? step() : true, timeout, name);
    }

    public void Enqueue(Func<bool?> step, int timeout, Action timedOut, string name)
    {
        var runId = _activeRunId;
        if (runId == 0)
            return;

        DateTime? deadline = null;
        _taskManager.Enqueue(() =>
        {
            if (!IsActive(runId))
                return true;

            deadline ??= DateTime.Now.AddMilliseconds(timeout);
            var result = step();
            if (result == true || DateTime.Now < deadline.Value)
                return result;

            timedOut();
            return true;
        }, name);
    }

    public void DelayNextImmediate(int milliseconds)
    {
        if (HasActiveRun)
            _taskManager.DelayNextImmediate(milliseconds);
    }

    public void WaitForNavmeshReadiness(Action completed, Action<AnimusNavigationResult> failed)
    {
        if (!HasActiveRun)
            return;

        StopNavmeshReadinessObserver();
        _readinessRunId = _activeRunId;
        _readinessCompleted = completed;
        _readinessFailed = failed;
        var now = DateTime.Now;
        _readinessGraceDeadline = now.AddSeconds(NavmeshReadinessGraceSeconds);
        _readinessStallDeadline = now.AddSeconds(NavmeshBuildStallSeconds);
        _readinessAbsoluteDeadline = now.AddSeconds(NavmeshReadinessAbsoluteSeconds);
        _readinessNextLogTime = now;
        _readinessProgress = float.NaN;

        if (!VNavmesh.Enabled)
        {
            CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshUnavailable, "vnavmesh is unavailable while waiting for navmesh readiness.");
            return;
        }

        try
        {
            if (VNavmesh.Nav.IsReady())
            {
                CompleteNavmeshReadiness();
                return;
            }
        }
        catch (Exception exception)
        {
            CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshUnavailable, $"vnavmesh readiness check failed: {exception.Message}");
            return;
        }

        State = AnimusAutomationRunState.AwaitingNavmeshReadiness;
        Svc.Framework.Update -= ObserveNavmeshReadiness;
        Svc.Framework.Update += ObserveNavmeshReadiness;
    }

    public bool TryStartNavigation(Vector3 destination, bool fly, Func<bool> paused, Action<AnimusNavigationResult> completed, float arrivalDistance = ArrivalDistance)
    {
        if (!HasActiveRun)
            return false;

        ReplaceNavigation();
        if (IsAtDestination(destination, arrivalDistance))
        {
            State = AnimusAutomationRunState.Arrived;
            completed(AnimusNavigationResult.Arrived);
            return true;
        }

        var player = Player.Object;
        if (player == null)
        {
            State = AnimusAutomationRunState.Failed;
            completed(AnimusNavigationResult.Rejected);
            return false;
        }

        var runId = _activeRunId;
        var cancellation = new CancellationTokenSource();
        Task<List<Vector3>> pathfindTask;
        try
        {
            pathfindTask = VNavmesh.Nav.PathfindCancelable(player.Position, destination, fly, cancellation.Token);
        }
        catch (Exception exception)
        {
            cancellation.Dispose();
            PluginLog.Verbose($"[ZodiacBuddy/NAV] Scoped navigation path request failed: {exception.Message}");
            State = AnimusAutomationRunState.Failed;
            completed(AnimusNavigationResult.Rejected);
            return false;
        }

        _navigationRunId = runId;
        _navigationDestination = destination;
        _navigationStartDeadline = DateTime.Now.AddSeconds(NavigationStartTimeoutSeconds);
        _navigationCompleted = completed;
        _navigationPaused = paused;
        _navigationPathfindTask = pathfindTask;
        _navigationPathfindCancellation = cancellation;
        _navigationFly = fly;
        _navigationArrivalDistance = Math.Max(0.1f, arrivalDistance);
        _navigationOwned = true;
        State = AnimusAutomationRunState.GeneratingPath;
        Svc.Framework.Update -= ObserveNavigation;
        Svc.Framework.Update += ObserveNavigation;
        return true;
    }

    public bool TryStartGroundGuidedFlight(Vector3 pathStart, Vector3 destination, float clearance, Func<bool> paused, Action<AnimusNavigationResult> completed)
    {
        if (!HasActiveRun)
            return false;

        ReplaceNavigation();
        if (IsAtDestination(destination))
        {
            State = AnimusAutomationRunState.Arrived;
            completed(AnimusNavigationResult.Arrived);
            return true;
        }

        var runId = _activeRunId;
        var cancellation = new CancellationTokenSource();
        Task<List<Vector3>> pathfindTask;
        try
        {
            pathfindTask = VNavmesh.Nav.PathfindCancelable(pathStart, destination, false, cancellation.Token);
        }
        catch (Exception exception)
        {
            cancellation.Dispose();
            PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight path request failed: {exception.Message}");
            State = AnimusAutomationRunState.Failed;
            completed(AnimusNavigationResult.Rejected);
            return false;
        }

        _navigationRunId = runId;
        _navigationDestination = destination;
        _navigationStartDeadline = DateTime.Now.AddSeconds(NavigationStartTimeoutSeconds);
        _navigationCompleted = completed;
        _navigationPaused = paused;
        _guidedPathfindTask = pathfindTask;
        _guidedPathfindCancellation = cancellation;
        _guidedFlightClearance = clearance;
        _navigationOwned = true;
        State = AnimusAutomationRunState.GeneratingPath;
        Svc.Framework.Update -= ObserveNavigation;
        Svc.Framework.Update += ObserveNavigation;
        return true;
    }

    public void ReplaceNavigation()
    {
        if (!_navigationOwned)
            return;

        StopOwnedProviderActivity();
        ClearOwnedNavigation();
        if (HasActiveRun)
            State = AnimusAutomationRunState.Active;
    }

    public void ClearPendingWork()
    {
        if (!HasActiveRun)
            return;

        StopNavmeshReadinessObserver();
        ReplaceNavigation();
#pragma warning disable CS0618
        _taskManager.Dispose();
#pragma warning restore CS0618
        _taskManager = CreateTaskManager();
        State = AnimusAutomationRunState.Active;
    }

    public void Cancel()
        => CancelCore(true);

    public void Dispose()
    {
        if (_disposed)
            return;

        CancelCore(false);
        _disposed = true;
    }

    private void CancelCore(bool resetTaskManager)
    {
        if (!HasActiveRun)
            return;

        StopNavmeshReadinessObserver();
        ReplaceNavigation();
        _activeRunId = 0;
        if (resetTaskManager)
        {
#pragma warning disable CS0618
            _taskManager.Dispose();
#pragma warning restore CS0618
            _taskManager = CreateTaskManager();
        }
        State = AnimusAutomationRunState.Cancelled;
    }

    private void ObserveNavmeshReadiness(IFramework _)
    {
        if (!IsActive(_readinessRunId))
        {
            StopNavmeshReadinessObserver();
            return;
        }
        if (!VNavmesh.Enabled)
        {
            CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshUnavailable, "vnavmesh became unavailable while waiting for navmesh readiness.");
            return;
        }

        try
        {
            if (VNavmesh.Nav.IsReady())
            {
                CompleteNavmeshReadiness();
                return;
            }

            var now = DateTime.Now;
            if (now >= _readinessAbsoluteDeadline)
            {
                CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshReadinessTimedOut, "navmesh readiness exceeded the absolute wait limit.");
                return;
            }

            var progress = VNavmesh.Nav.BuildProgress();
            if (progress >= 0)
            {
                if (float.IsNaN(_readinessProgress) || progress > _readinessProgress)
                {
                    _readinessProgress = progress;
                    _readinessStallDeadline = now.AddSeconds(NavmeshBuildStallSeconds);
                }
                if (now >= _readinessStallDeadline)
                {
                    CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshReadinessTimedOut, $"navmesh build stalled at {_readinessProgress:P1}.");
                    return;
                }
                if (now >= _readinessNextLogTime)
                {
                    PluginLog.Verbose($"[ZodiacBuddy/NAV] Waiting for navmesh build progress: {progress:P1}.");
                    _readinessNextLogTime = now.AddSeconds(NavmeshProgressLogSeconds);
                }
                return;
            }

            if (now >= _readinessGraceDeadline)
                CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshReadinessTimedOut, "navmesh did not become ready and no active build was observed.");
        }
        catch (Exception exception)
        {
            CompleteNavmeshReadinessFailure(AnimusNavigationResult.NavmeshUnavailable, $"vnavmesh readiness observation failed: {exception.Message}");
        }
    }

    private void CompleteNavmeshReadiness()
    {
        var runId = _readinessRunId;
        var completed = _readinessCompleted;
        StopNavmeshReadinessObserver();
        if (!IsActive(runId))
            return;

        State = AnimusAutomationRunState.Active;
        completed?.Invoke();
    }

    private void CompleteNavmeshReadinessFailure(AnimusNavigationResult result, string message)
    {
        var runId = _readinessRunId;
        var failed = _readinessFailed;
        StopNavmeshReadinessObserver();
        if (!IsActive(runId))
            return;

        State = AnimusAutomationRunState.Failed;
        PluginLog.Warning($"[ZodiacBuddy/NAV] {message}");
        failed?.Invoke(result);
    }

    private void ObserveNavigation(IFramework _)
    {
        if (!_navigationOwned || !IsActive(_navigationRunId))
        {
            ClearOwnedNavigation();
            return;
        }
        if (_navigationPaused?.Invoke() == true)
            return;

        if (IsAtDestination(_navigationDestination, _navigationArrivalDistance))
        {
            CompleteNavigation(AnimusNavigationResult.Arrived);
            return;
        }

        if (_navigationPathfindTask != null)
        {
            if (!_navigationPathfindTask.IsCompleted)
            {
                State = AnimusAutomationRunState.GeneratingPath;
                return;
            }

            if (_navigationPathfindTask.IsCanceled)
            {
                CompleteNavigation(AnimusNavigationResult.Cancelled);
                return;
            }

            List<Vector3> waypoints;
            try
            {
                waypoints = _navigationPathfindTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                PluginLog.Verbose($"[ZodiacBuddy/NAV] Scoped navigation path generation failed: {exception.Message}");
                CompleteNavigation(AnimusNavigationResult.Rejected);
                return;
            }

            if (waypoints.Count == 0)
            {
                PluginLog.Verbose("[ZodiacBuddy/NAV] Scoped navigation path generation returned no waypoints.");
                CompleteNavigation(AnimusNavigationResult.Rejected);
                return;
            }

            _navigationPathfindTask = null;
            _navigationPathfindCancellation?.Dispose();
            _navigationPathfindCancellation = null;
            try
            {
                VNavmesh.Path.MoveTo(waypoints, _navigationFly);
            }
            catch (Exception exception)
            {
                PluginLog.Verbose($"[ZodiacBuddy/NAV] Scoped navigation could not start waypoint following: {exception.Message}");
                CompleteNavigation(AnimusNavigationResult.Rejected);
                return;
            }

            _navigationStartDeadline = DateTime.Now.AddSeconds(NavigationStartTimeoutSeconds);
            State = AnimusAutomationRunState.AwaitingNavigationStart;
            PluginLog.Verbose($"[ZodiacBuddy/NAV] Scoped navigation path started with {waypoints.Count} waypoints; fly={_navigationFly}.");
            return;
        }

        if (_guidedPathfindTask != null)
        {
            if (!_guidedPathfindTask.IsCompleted)
            {
                State = AnimusAutomationRunState.GeneratingPath;
                return;
            }

            if (_guidedPathfindTask.IsCanceled)
            {
                CompleteNavigation(AnimusNavigationResult.Cancelled);
                return;
            }

            List<Vector3> groundWaypoints;
            try
            {
                groundWaypoints = _guidedPathfindTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight path generation failed: {exception.Message}");
                CompleteNavigation(AnimusNavigationResult.Rejected);
                return;
            }

            if (groundWaypoints.Count == 0)
            {
                PluginLog.Verbose("[ZodiacBuddy/NAV] Ground-guided flight path generation returned no waypoints.");
                CompleteNavigation(AnimusNavigationResult.Rejected);
                return;
            }

            var flightWaypoints = new List<Vector3>(groundWaypoints.Count);
            foreach (var waypoint in groundWaypoints)
                flightWaypoints.Add(new Vector3(waypoint.X, waypoint.Y + _guidedFlightClearance, waypoint.Z));

            _guidedPathfindTask = null;
            _guidedPathfindCancellation?.Dispose();
            _guidedPathfindCancellation = null;
            try
            {
                VNavmesh.Path.MoveTo(flightWaypoints, true);
            }
            catch (Exception exception)
            {
                PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight could not start waypoint following: {exception.Message}");
                CompleteNavigation(AnimusNavigationResult.Rejected);
                return;
            }
            _navigationStartDeadline = DateTime.Now.AddSeconds(NavigationStartTimeoutSeconds);
            State = AnimusAutomationRunState.AwaitingNavigationStart;
            PluginLog.Verbose($"[ZodiacBuddy/NAV] Ground-guided flight path started with {flightWaypoints.Count} waypoints.");
            return;
        }

        var followingPath = VNavmesh.Path.IsRunning();
        if (followingPath)
        {
            State = AnimusAutomationRunState.FollowingPath;
            return;
        }
        if (State == AnimusAutomationRunState.AwaitingNavigationStart)
        {
            if (DateTime.Now >= _navigationStartDeadline)
                CompleteNavigation(AnimusNavigationResult.StartupTimedOut);
            return;
        }
        if (State is AnimusAutomationRunState.GeneratingPath or AnimusAutomationRunState.FollowingPath)
            CompleteNavigation(AnimusNavigationResult.StoppedBeforeArrival);
    }

    private bool IsAtDestination(Vector3 destination, float arrivalDistance = ArrivalDistance)
    {
        var player = Player.Object;
        return player != null && Vector3.Distance(player.Position, destination) <= arrivalDistance;
    }

    private void CompleteNavigation(AnimusNavigationResult result)
    {
        var runId = _navigationRunId;
        var completed = _navigationCompleted;
        if (result == AnimusNavigationResult.Arrived)
            StopOwnedProviderActivity();
        ClearOwnedNavigation();

        if (!IsActive(runId))
            return;

        State = result switch
        {
            AnimusNavigationResult.Arrived => AnimusAutomationRunState.Arrived,
            AnimusNavigationResult.Cancelled => AnimusAutomationRunState.Cancelled,
            _ => AnimusAutomationRunState.Failed
        };
        completed?.Invoke(result);
    }

    private void StopOwnedProviderActivity()
    {
        if (_navigationPathfindCancellation != null)
        {
            _navigationPathfindCancellation.Cancel();
            return;
        }
        if (_guidedPathfindCancellation != null)
        {
            _guidedPathfindCancellation.Cancel();
            return;
        }
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();
    }

    private void StopNavmeshReadinessObserver()
    {
        Svc.Framework.Update -= ObserveNavmeshReadiness;
        _readinessRunId = 0;
        _readinessCompleted = null;
        _readinessFailed = null;
    }

    private void ClearOwnedNavigation()
    {
        StopNavigationObserver();
        _navigationPathfindCancellation?.Dispose();
        _navigationPathfindCancellation = null;
        _navigationPathfindTask = null;
        _navigationFly = false;
        _navigationArrivalDistance = ArrivalDistance;
        _guidedPathfindCancellation?.Dispose();
        _guidedPathfindCancellation = null;
        _guidedPathfindTask = null;
        _guidedFlightClearance = 0;
        _navigationOwned = false;
        _navigationRunId = 0;
        _navigationCompleted = null;
        _navigationPaused = null;
    }

    private void StopNavigationObserver()
        => Svc.Framework.Update -= ObserveNavigation;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AnimusAutomationRun));
    }
}
