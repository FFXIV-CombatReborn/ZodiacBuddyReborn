using System;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private bool _experimentalGrinderTargetFreelyActive;
    private bool _experimentalGrinderPresetValidated;
    private bool _experimentalGrinderPresetRejectedForRun;
    private string _experimentalGrinderFallbackLogKey = string.Empty;
    private DateTime _experimentalGrinderRsrTargetMissingSince = DateTime.MinValue;
    private ulong _experimentalGrinderMacroAnchorId;
    private ulong _experimentalGrinderPendingMacroTargetId;
    private DateTime _experimentalGrinderPendingMacroTargetSince = DateTime.MinValue;
    private ulong _experimentalGrinderPullCandidateId;
    private DateTime _experimentalGrinderPullCandidateSince = DateTime.MinValue;
    private DateTime _experimentalGrinderPullProbeUntil = DateTime.MinValue;
    private ulong _experimentalGrinderSuppressedPullCandidateId;
    private DateTime _experimentalGrinderSuppressedPullCandidateUntil = DateTime.MinValue;
    private DateTime _experimentalGrinderNextPullAt = DateTime.MinValue;
    private string _experimentalGrinderPullHoldReason = string.Empty;
    private DateTime _experimentalGrinderRsrStartupConfirmedSince = DateTime.MinValue;
    private bool _experimentalGrinderRsrStartupHandoffCompleted;

    private void ResetExperimentalGrinderCombatForNewRun()
    {
        DisableExperimentalGrinderTargetFreely("a new FATE execution cycle is starting");
        _experimentalGrinderPresetValidated = false;
        _experimentalGrinderPresetRejectedForRun = false;
        _experimentalGrinderFallbackLogKey = string.Empty;
        _experimentalGrinderRsrStartupConfirmedSince = DateTime.MinValue;
        _experimentalGrinderRsrStartupHandoffCompleted = false;
        ResetExperimentalGrinderRetentionForNewRun();
        ResetExperimentalGrinderTargetOwnershipState();
    }

    private void ResetExperimentalGrinderTargetOwnershipState()
    {
        _experimentalGrinderRsrTargetMissingSince = DateTime.MinValue;
        _experimentalGrinderMacroAnchorId = 0;
        _experimentalGrinderPendingMacroTargetId = 0;
        _experimentalGrinderPendingMacroTargetSince = DateTime.MinValue;
        ClearExperimentalGrinderPullCandidate();
        _experimentalGrinderSuppressedPullCandidateId = 0;
        _experimentalGrinderSuppressedPullCandidateUntil = DateTime.MinValue;
        _experimentalGrinderNextPullAt = DateTime.MinValue;
        _experimentalGrinderPullHoldReason = string.Empty;
    }

    private bool EnsureExperimentalGrinderPresetReady(out string detail)
    {
        if (_experimentalGrinderPresetValidated)
        {
            detail = "RSR grinder preset already validated for this FATE run.";
            return true;
        }

        if (_experimentalGrinderPresetRejectedForRun)
        {
            detail = "RSR grinder preset was already rejected for this FATE run.";
            return false;
        }

        if (!FateGrinderRsrPresetManager.AreDesiredSettingsActive(out detail))
        {
            _experimentalGrinderPresetRejectedForRun = true;
            return false;
        }

        _experimentalGrinderPresetValidated = true;
        return true;
    }

    private void StartFateRotationSolver()
    {
        DisableExperimentalGrinderTargetFreely("legacy FATE combat requested manual RSR ownership");
        ResetExperimentalGrinderTargetOwnershipState();
        _fateRotationSolver.Acquire(
            "[ZodiacBuddy/FATE] RSR manual mode enabled.",
            "[ZodiacBuddy/FATE] RSR manual mode enabled by command fallback.");
    }

    private bool EnsureExperimentalGrinderRotationSolver(RotationSolverTargetingType targetingType)
    {
        if (_fateContext.Request.Purpose != FateExecutionPurpose.GeneralGrinder
            || !Service.Configuration.FateGrinder.ExperimentalCombat)
            return false;

        if (!_fateRotationSolver.AcquireAutoDuty(
                targetingType,
                $"[ZodiacBuddy/GRINDER-COMBAT] RSR AutoDuty enabled, targeting={targetingType}."))
            return false;

        if (!_experimentalGrinderTargetFreelyActive)
        {
            if (!RSRIPC.TrySetTargetFreelyOverride(true))
            {
                _fateRotationSolver.ReleaseNow(
                    "[ZodiacBuddy/GRINDER-COMBAT] RSR disabled after TargetFreely failed.",
                    "[ZodiacBuddy/GRINDER-COMBAT] RSR disable command used after TargetFreely failed.");
                return false;
            }

            _experimentalGrinderTargetFreelyActive = true;
            Service.PluginLog.Verbose("[ZodiacBuddy/GRINDER-COMBAT] TargetFreely enabled.");
        }

        return true;
    }

    private void DisableExperimentalGrinderTargetFreely(string reason)
    {
        if (!_experimentalGrinderTargetFreelyActive)
            return;

        if (!RSRIPC.IsLoaded)
        {
            _experimentalGrinderTargetFreelyActive = false;
            Service.PluginLog.Verbose("[ZodiacBuddy/GRINDER-COMBAT] TargetFreely ownership cleared; RSR unloaded.");
            return;
        }

        if (RSRIPC.TrySetTargetFreelyOverride(false))
        {
            _experimentalGrinderTargetFreelyActive = false;
            Service.PluginLog.Verbose($"[ZodiacBuddy/GRINDER-COMBAT] TargetFreely disabled: {reason}.");
        }
        else
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/GRINDER-COMBAT] Could not disable TargetFreely: {reason}; will retry.");
        }
    }


    private void LogExperimentalGrinderFallbackOnce(string key, string detail)
    {
        if (string.Equals(_experimentalGrinderFallbackLogKey, key, System.StringComparison.Ordinal))
            return;

        _experimentalGrinderFallbackLogKey = key;
        Service.PluginLog.Warning($"[ZodiacBuddy/GRINDER-COMBAT] Combat fallback: {detail}");
    }

    private void ReleaseExperimentalGrinderCombatSession(string reason, bool releaseRotationSolver)
    {
        DisableExperimentalGrinderTargetFreely(reason);
        ResetExperimentalGrinderTargetOwnershipState();
        if (releaseRotationSolver && _fateRotationSolver.IsOwned)
        {
            _fateRotationSolver.ReleaseNow(
                $"[ZodiacBuddy/GRINDER-COMBAT] RSR disabled: {reason}.",
                $"[ZodiacBuddy/GRINDER-COMBAT] RSR disable command used: {reason}.");
        }
    }

    private void ReleaseFateRotationSolverNow(string reason)
    {
        DisableExperimentalGrinderTargetFreely(reason);
        ResetExperimentalGrinderTargetOwnershipState();
        _fateRotationSolver.ReleaseNow(
            $"[ZodiacBuddy/FATE] RSR disabled: {reason}.",
            $"[ZodiacBuddy/FATE] RSR disable command used: {reason}.");
    }

    private void RequestFateRotationSolverStop(string reason)
    {
        DisableExperimentalGrinderTargetFreely(reason);
        ResetExperimentalGrinderTargetOwnershipState();
        _fateRotationSolver.RequestStop(
            $"[ZodiacBuddy/FATE] RSR stop requested: {reason}.",
            "[ZodiacBuddy/FATE] RSR disabled.");
    }

    private void ProcessFateRotationSolverStopRetry()
    {
        _fateRotationSolver.ProcessStopRetry("[ZodiacBuddy/FATE] RSR disabled.");
    }
}
