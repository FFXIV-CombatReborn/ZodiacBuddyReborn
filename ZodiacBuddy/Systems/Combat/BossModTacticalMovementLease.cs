using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZodiacBuddy.Systems.Combat;

internal sealed class BossModTacticalMovementLease
{
    internal const float CloseRangeDistance = 2.6f;
    private const float CloseRangeCaptureDistance = 0.8f;
    private const float RangedDistance = 10f;

    private static readonly HashSet<uint> CloseRangeClassJobs =
    [
        1, 2, 3, 4,
        19, 20, 21, 22,
        29, 30, 32, 34,
        37, 39, 41,
    ];

    private Snapshot? snapshot;
    private float configuredRange;
    private bool moveDelayModified;
    private string? lastUnsupportedMoveDelay;

    internal bool IsOwned => snapshot != null;
    internal float ConfiguredRange => configuredRange;
    internal static bool IsCloseRangeClassJob(uint classJobId) => CloseRangeClassJobs.Contains(classJobId);

    internal bool Acquire(uint classJobId, bool useMeleeCaptureRange = false)
    {
        var closeRange = IsCloseRangeClassJob(classJobId);
        var requestedRange = closeRange
            ? useMeleeCaptureRange ? CloseRangeCaptureDistance : CloseRangeDistance
            : RangedDistance;
        if (snapshot != null)
        {
            if (MathF.Abs(configuredRange - requestedRange) < 0.05f)
                return true;

            if (!BossModIPC.TrySetConfiguration("MaxDistanceToTarget", FormatFloat(requestedRange)))
                return false;

            configuredRange = requestedRange;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] BossMod tactical movement range adjusted to {requestedRange:F1}y{(useMeleeCaptureRange && closeRange ? " for initial melee capture" : string.Empty)}.");
            return true;
        }

        if (!BossModIPC.IsLoaded || !TryCaptureSnapshot(out var captured))
            return false;

        if (!TryParseDouble(captured.MoveDelay, out var moveDelay))
            return false;

        moveDelayModified = false;
        if (Math.Abs(moveDelay) > 0.001d)
        {
            if (!BossModIPC.TrySetConfiguration("MoveDelay", "0"))
            {
                if (!string.Equals(lastUnsupportedMoveDelay, captured.MoveDelay, StringComparison.Ordinal))
                {
                    lastUnsupportedMoveDelay = captured.MoveDelay;
                    Service.PluginLog.Warning($"[ZodiacBuddy/FATE] BossMod MoveDelay is {captured.MoveDelay}, but this BMR build cannot temporarily write its double-valued MoveDelay field through BossMod.Configuration. ZBR will use the vnav combat fallback instead of leasing BMR with user-dependent movement delay.");
                }
                return false;
            }

            moveDelayModified = true;
        }

        snapshot = captured;
        if (!ApplyMovementOnlyConfiguration(requestedRange))
        {
            RestoreSnapshot(captured, false, moveDelayModified);
            snapshot = null;
            moveDelayModified = false;
            return false;
        }

        try
        {
            Service.CommandManager.ProcessCommand("/bmrai on");
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not enable BossMod AI: {exception.Message}");
            RestoreSnapshot(captured, false, moveDelayModified);
            snapshot = null;
            moveDelayModified = false;
            return false;
        }

        configuredRange = requestedRange;
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] BossMod tactical movement acquired at {requestedRange:F1}y{(useMeleeCaptureRange && closeRange ? " for initial melee capture" : string.Empty)}; actions and autonomous targeting are suppressed; obstacle maps are enabled for the lease.");
        return true;
    }

    internal void Release()
    {
        if (snapshot is not Snapshot captured)
            return;

        try
        {
            Service.CommandManager.ProcessCommand("/bmrai off");
        }
        catch (Exception exception)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] Could not disable BossMod AI during tactical movement release: {exception.Message}");
        }

        RestoreSnapshot(captured, true, moveDelayModified);
        snapshot = null;
        configuredRange = 0f;
        moveDelayModified = false;
        Service.PluginLog.Verbose("[ZodiacBuddy/FATE] BossMod tactical movement released and temporary AI settings restored.");
    }

    private static bool TryCaptureSnapshot(out Snapshot captured)
    {
        captured = default;
        if (!TryGetInt("FollowSlot", out var followSlot)
            || !TryGetBool("ForbidActions", out var forbidActions)
            || !TryGetBool("ManualTarget", out var manualTarget)
            || !TryGetBool("ForbidMovement", out var forbidMovement)
            || !TryGetBool("FollowDuringCombat", out var followDuringCombat)
            || !TryGetBool("FollowDuringActiveBossModule", out var followDuringActiveBossModule)
            || !TryGetBool("FollowOutOfCombat", out var followOutOfCombat)
            || !TryGetBool("FollowTarget", out var followTarget)
            || !TryGetBool("DisableObstacleMaps", out var disableObstacleMaps)
            || !TryGetFloat("MaxDistanceToTarget", out var maxDistanceToTarget)
            || !TryGetRaw("DesiredPositional", out var desiredPositional)
            || !TryGetRaw("MinDistance", out var minDistance)
            || !TryGetRaw("PreferredDistance", out var preferredDistance)
            || !TryGetRaw("MoveDelay", out var moveDelay)
            || !TryGetRaw("FollowRSRDesiredPositional", out var followRsrDesiredPositional)
            || !TryGetRaw("AutoAFK", out var autoAfk)
            || !BossModIPC.TryGetAiPreset(out var aiPreset)
            || !BossModIPC.TryGetActivePreset(out var activePreset))
            return false;

        captured = new Snapshot(
            followSlot,
            forbidActions,
            manualTarget,
            forbidMovement,
            followDuringCombat,
            followDuringActiveBossModule,
            followOutOfCombat,
            followTarget,
            disableObstacleMaps,
            maxDistanceToTarget,
            desiredPositional,
            minDistance,
            preferredDistance,
            moveDelay,
            followRsrDesiredPositional,
            autoAfk,
            aiPreset,
            activePreset);
        return true;
    }

    private static bool ApplyMovementOnlyConfiguration(float range)
    {
        return BossModIPC.TrySetConfiguration("FollowSlot", "0")
            && BossModIPC.TrySetConfiguration("ForbidActions", "true")
            && BossModIPC.TrySetConfiguration("ManualTarget", "true")
            && BossModIPC.TrySetConfiguration("ForbidMovement", "false")
            && BossModIPC.TrySetConfiguration("FollowDuringCombat", "true")
            && BossModIPC.TrySetConfiguration("FollowDuringActiveBossModule", "true")
            && BossModIPC.TrySetConfiguration("FollowOutOfCombat", "true")
            && BossModIPC.TrySetConfiguration("FollowTarget", "true")
            && BossModIPC.TrySetConfiguration("DisableObstacleMaps", "false")
            && BossModIPC.TrySetConfiguration("MaxDistanceToTarget", FormatFloat(range))
            && BossModIPC.TrySetConfiguration("DesiredPositional", "Any")
            && BossModIPC.TrySetConfiguration("MinDistance", "0")
            && BossModIPC.TrySetConfiguration("PreferredDistance", "0")
            && BossModIPC.TrySetConfiguration("FollowRSRDesiredPositional", "false")
            && BossModIPC.TrySetConfiguration("AutoAFK", "false")
            && BossModIPC.TrySetAiPreset(string.Empty);
    }

    private static void RestoreSnapshot(Snapshot captured, bool restoreActivePreset, bool restoreMoveDelay)
    {
        var restored = BossModIPC.TrySetConfiguration("FollowSlot", captured.FollowSlot.ToString(CultureInfo.CurrentCulture))
            & BossModIPC.TrySetConfiguration("ForbidActions", captured.ForbidActions.ToString())
            & BossModIPC.TrySetConfiguration("ManualTarget", captured.ManualTarget.ToString())
            & BossModIPC.TrySetConfiguration("ForbidMovement", captured.ForbidMovement.ToString())
            & BossModIPC.TrySetConfiguration("FollowDuringCombat", captured.FollowDuringCombat.ToString())
            & BossModIPC.TrySetConfiguration("FollowDuringActiveBossModule", captured.FollowDuringActiveBossModule.ToString())
            & BossModIPC.TrySetConfiguration("FollowOutOfCombat", captured.FollowOutOfCombat.ToString())
            & BossModIPC.TrySetConfiguration("FollowTarget", captured.FollowTarget.ToString())
            & BossModIPC.TrySetConfiguration("DisableObstacleMaps", captured.DisableObstacleMaps.ToString())
            & BossModIPC.TrySetConfiguration("MaxDistanceToTarget", FormatFloat(captured.MaxDistanceToTarget))
            & BossModIPC.TrySetConfiguration("DesiredPositional", captured.DesiredPositional)
            & BossModIPC.TrySetConfiguration("MinDistance", captured.MinDistance)
            & BossModIPC.TrySetConfiguration("PreferredDistance", captured.PreferredDistance)
            & BossModIPC.TrySetConfiguration("FollowRSRDesiredPositional", captured.FollowRsrDesiredPositional)
            & BossModIPC.TrySetConfiguration("AutoAFK", captured.AutoAfk)
            & BossModIPC.TrySetAiPreset(captured.AiPreset);

        if (restoreMoveDelay)
            restored &= BossModIPC.TrySetConfiguration("MoveDelay", captured.MoveDelay);

        if (restoreActivePreset && !string.IsNullOrEmpty(captured.ActivePreset))
            restored &= BossModIPC.TryRestoreActivePreset(captured.ActivePreset);

        if (!restored)
            Service.PluginLog.Warning("[ZodiacBuddy/FATE] One or more temporary BossMod AI settings could not be restored.");
    }

    private static bool TryGetBool(string field, out bool value)
    {
        value = false;
        return BossModIPC.TryGetConfiguration(field, out var raw) && bool.TryParse(raw, out value);
    }

    private static bool TryGetInt(string field, out int value)
    {
        value = 0;
        return BossModIPC.TryGetConfiguration(field, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryGetFloat(string field, out float value)
    {
        value = 0f;
        if (!BossModIPC.TryGetConfiguration(field, out var raw))
            return false;

        return float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetRaw(string field, out string value)
        => BossModIPC.TryGetConfiguration(field, out value);

    private static bool TryParseDouble(string raw, out double value)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string FormatFloat(float value)
        => value.ToString(CultureInfo.CurrentCulture);

    private readonly record struct Snapshot(
        int FollowSlot,
        bool ForbidActions,
        bool ManualTarget,
        bool ForbidMovement,
        bool FollowDuringCombat,
        bool FollowDuringActiveBossModule,
        bool FollowOutOfCombat,
        bool FollowTarget,
        bool DisableObstacleMaps,
        float MaxDistanceToTarget,
        string DesiredPositional,
        string MinDistance,
        string PreferredDistance,
        string MoveDelay,
        string FollowRsrDesiredPositional,
        string AutoAfk,
        string AiPreset,
        string? ActivePreset);
}
