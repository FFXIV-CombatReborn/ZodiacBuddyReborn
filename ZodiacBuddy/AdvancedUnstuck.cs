using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using ECommons.MathHelpers;
using System;
using System.Collections.Generic;
using System.Numerics;
using ZodiacBuddy.Stages.Atma.Movement;

namespace ZodiacBuddy.Stages.Atma.Unstuck;

public enum AdvancedUnstuckCheckResult
{
    Pass,
    Wait,
    Fail
}

public sealed class AdvancedUnstuck : IDisposable
{
    public event Action? OnUnstuckCompleted;
    private const double UnstuckDuration = 1.0;
    private const double CheckExpiration = 1.0;
    private const float MinMovementDistance = 2.0f;
    private const double NavResetCooldown = 5.0;  // seconds cooldown before checking unstuck again
    private const double NavResetThreshold = 3.0; // seconds stuck before triggering unstuck

    private readonly OverrideMovement _movementController = new();
    private DateTime _lastMovement;
    private DateTime _unstuckStart;
    private DateTime _lastCheck;
    private Vector3 _lastPosition;
    private bool _lastWasFailure;

    public bool IsRunning => _movementController.Enabled;

    public Action? OnUnstuckComplete { get; internal set; }

    public AdvancedUnstuckCheckResult Check(Vector3 destination, bool isPathGenerating, bool isPathing)
    {
        if (IsRunning)
            return AdvancedUnstuckCheckResult.Fail;

        var now = DateTime.Now;

        // On cooldown, not navigating or near the destination: disable tracking and reset
        if (now.Subtract(_unstuckStart).TotalSeconds < NavResetCooldown
            || destination == default
            || Vector2.Distance(destination.ToVector2(), Player.Position.ToVector2()) < 3.5)
        {
            _lastCheck = DateTime.MinValue;
            return AdvancedUnstuckCheckResult.Pass;
        }

        var lastCheck = _lastCheck;
        _lastCheck = now;

        // Tracking wasn't active for 1 second or was reset: restart tracking from the current position
        if (now.Subtract(lastCheck).TotalSeconds > CheckExpiration)
        {
            _lastPosition = Player.Position;
            _lastMovement = now;
            _lastWasFailure = false;
            return AdvancedUnstuckCheckResult.Pass;
        }

        // vnavmesh is generating path: update current position
        if (isPathGenerating)
        {
            _lastPosition = Player.Position;
            _lastMovement = now;
        }
        // vnavmesh is moving...
        else if (isPathing)
        {
            // ...and quite fast: update current position
            if (Vector3.Distance(_lastPosition, Player.Position) >= MinMovementDistance)
            {
                _lastPosition = Player.Object.Position;
                _lastMovement = now;
            }
            // ...but not fast enough: unstuck
            else if (now.Subtract(_lastMovement).TotalSeconds > NavResetThreshold)
            {
                Start();
            }
        }
        // Not generating path and not moving for 2 consecutive framework updates: unstuck
        else if (_lastWasFailure)
        {
            Console.WriteLine($"Advanced Unstuck: vnavmesh failure detected.");
            Start();
        }

        // Not generating path and not moving: remember that fact and exit main loop
        _lastWasFailure = !isPathGenerating && !isPathing;
        return IsRunning ? AdvancedUnstuckCheckResult.Fail : _lastWasFailure ? AdvancedUnstuckCheckResult.Wait : AdvancedUnstuckCheckResult.Pass;
    }

    public void Force()
    {
        if (!IsRunning)
        {
            Console.WriteLine("Advanced Unstuck: force start.");
            Start();
        }
    }

    public void Start()
    {
        if (!IsRunning)
        {
            var rng = new Random();
            float rnd() => (rng.Next(2) == 0 ? -1 : 1) * rng.NextSingle();
            var newPosition = Player.Position + Vector3.Normalize(new Vector3(rnd(), 0, rnd())) * 5f;

            _movementController.DesiredPosition = newPosition;

            //Use correct MoveTo overload
            VNavmesh.Path.MoveTo(new List<Vector3> { newPosition }, false);

            _lastPosition = Player.Object?.Position ?? Vector3.Zero;
            _lastMovement = _unstuckStart;
            _movementController.Enabled = true;
            _unstuckStart = DateTime.Now;
            Svc.Framework.Update += RunningUpdate;

            PluginLog.Debug($"AdvancedUnstuck: Initiating movement to {newPosition}");
        }
    }

    public void RunningUpdate(IFramework framework)
    {
        if (!_movementController.Enabled) return; // Only do logic if active
        if (DateTime.Now.Subtract(_unstuckStart).TotalSeconds > UnstuckDuration)
        {
            Stop();
        }
    }

    private void Stop()
    {
        if (IsRunning)
        {
            _movementController.Enabled = false;
            Svc.Framework.Update -= RunningUpdate;
            PluginLog.Debug("[ZodiacBuddy] AdvancedUnstuck: Movement override stopped.");

            //Trigger post-unstuck callback
            OnUnstuckCompleted?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        _movementController.Dispose();
    }
}