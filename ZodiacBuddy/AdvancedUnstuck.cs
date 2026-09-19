using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using System;
using System.Numerics;

namespace ZodiacBuddy;

public sealed class AdvancedUnstuck : IDisposable
{
    public event Action? Completed;
    private const double UnstuckDuration = 1.0;
    private readonly OverrideMovement _movementController = new();
    private DateTime _unstuckStart;

    public bool IsRunning => _movementController.Enabled;

    public void Start()
    {
        if (!IsRunning)
        {
            var rng = new Random();
            float rnd() => (rng.Next(2) == 0 ? -1 : 1) * rng.NextSingle();
            var newPosition = Player.Position + (Vector3.Normalize(new Vector3(rnd(), 0, rnd())) * 5f);

            _movementController.DesiredPosition = newPosition;

            //Use correct MoveTo overload
            VNavmesh.Path.MoveTo([newPosition], false);

            _movementController.Enabled = true;
            _unstuckStart = DateTime.Now;
            Svc.Framework.Update += RunningUpdate;

            PluginLog.Verbose($"[ZodiacBuddy/NAV] AdvancedUnstuck: Initiating movement to {newPosition}");
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

    public void Cancel()
    {
        if (!IsRunning)
            return;

        VNavmesh.Path.Stop();
        Deactivate();
    }

    private void Stop()
    {
        if (!IsRunning)
            return;

        Deactivate();
        Completed?.Invoke();
    }

    private void Deactivate()
    {
        _movementController.Enabled = false;
        Svc.Framework.Update -= RunningUpdate;
        PluginLog.Verbose("[ZodiacBuddy/NAV] AdvancedUnstuck: Movement override stopped.");
    }

    public void Dispose()
    {
        Cancel();
        _movementController.Dispose();
    }
}