using System;

namespace ZodiacBuddy.Systems.Combat;

internal sealed class RotationSolverLease
{
    private static readonly TimeSpan StopRetryWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopRetryInterval = TimeSpan.FromMilliseconds(500);

    private bool owned;
    private RotationSolverOperatingMode ownedMode = RotationSolverOperatingMode.Off;
    private RotationSolverTargetingType? autoDutyTargetingType;
    private DateTime stopRetryUntil = DateTime.MinValue;
    private DateTime nextStopAttempt = DateTime.MinValue;

    internal bool IsOwned => owned;
    internal bool IsAutoDutyOwned => owned && ownedMode == RotationSolverOperatingMode.AutoDuty;
    internal bool HasPendingStop => stopRetryUntil != DateTime.MinValue;

    internal bool Acquire(string successMessage, string fallbackMessage)
    {
        if (!RSRIPC.IsLoaded)
            return false;

        ClearStopRetry();
        if (owned && ownedMode == RotationSolverOperatingMode.Manual)
            return false;

        if (RSRIPC.TrySetOperatingMode(RotationSolverOperatingMode.Manual))
        {
            owned = true;
            ownedMode = RotationSolverOperatingMode.Manual;
            autoDutyTargetingType = null;
            Service.PluginLog.Verbose(successMessage);
            return true;
        }

        Service.CommandManager.ProcessCommand("/rotation manual");
        owned = true;
        ownedMode = RotationSolverOperatingMode.Manual;
        autoDutyTargetingType = null;
        Service.PluginLog.Verbose(fallbackMessage);
        return true;
    }

    internal bool AcquireAutoDuty(RotationSolverTargetingType targetingType, string successMessage)
    {
        if (!RSRIPC.SupportsGrinderAutoMode)
            return false;

        ClearStopRetry();
        if (owned
            && ownedMode == RotationSolverOperatingMode.AutoDuty
            && autoDutyTargetingType == targetingType)
            return true;

        if (!RSRIPC.TrySetAutoDutyOperatingMode(targetingType))
            return false;

        owned = true;
        ownedMode = RotationSolverOperatingMode.AutoDuty;
        autoDutyTargetingType = targetingType;
        Service.PluginLog.Verbose(successMessage);
        return true;
    }

    internal void ReleaseNow(string ipcMessage, string fallbackMessage)
    {
        if (!owned && !HasPendingStop)
            return;

        if (RSRIPC.IsLoaded && RSRIPC.TrySetOperatingMode(RotationSolverOperatingMode.Off))
            Service.PluginLog.Verbose(ipcMessage);
        else
        {
            Service.CommandManager.ProcessCommand("/rotation off");
            Service.PluginLog.Verbose(fallbackMessage);
        }

        ClearOwnership();
    }

    internal void RequestStop(string requestMessage, string successMessage, string? expiryWarning = null)
    {
        if (!owned && !HasPendingStop)
            return;

        if (!RSRIPC.IsLoaded)
        {
            ClearOwnership();
            return;
        }

        stopRetryUntil = DateTime.Now.Add(StopRetryWindow);
        nextStopAttempt = DateTime.MinValue;
        Service.PluginLog.Verbose(requestMessage);
        ProcessStopRetry(successMessage, expiryWarning);
    }

    internal void ProcessStopRetry(string successMessage, string? expiryWarning = null)
    {
        if (!HasPendingStop || DateTime.Now < nextStopAttempt)
            return;

        if (!RSRIPC.IsLoaded)
        {
            ClearOwnership();
            return;
        }

        if (RSRIPC.TrySetOperatingMode(RotationSolverOperatingMode.Off))
        {
            ClearOwnership();
            Service.PluginLog.Verbose(successMessage);
            return;
        }

        Service.CommandManager.ProcessCommand("/rotation off");
        nextStopAttempt = DateTime.Now.Add(StopRetryInterval);
        if (DateTime.Now < stopRetryUntil)
            return;

        ClearOwnership();
        if (!string.IsNullOrWhiteSpace(expiryWarning))
            Service.PluginLog.Warning(expiryWarning);
    }

    private void ClearOwnership()
    {
        owned = false;
        ownedMode = RotationSolverOperatingMode.Off;
        autoDutyTargetingType = null;
        ClearStopRetry();
    }

    private void ClearStopRetry()
    {
        stopRetryUntil = DateTime.MinValue;
        nextStopAttempt = DateTime.MinValue;
    }
}
