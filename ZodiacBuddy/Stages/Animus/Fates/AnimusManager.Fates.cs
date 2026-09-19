using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Combat;
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const float FateInteractDistance = 5.5f;
    private const float FateCombatEngageDistance = 5f;
    private const float FateCombatMacroHandoffDistance = 20f;
    private const float FateCombatMacroReacquireMargin = 5f;
    private const float FateCombatElevationAcquireTolerance = 2.5f;
    private const float FateCombatElevationRecoveryTolerance = 4f;
    private const float FateCombatTargetGroundSearchVertical = 24f;
    private const float FateTacticalEngageSlack = 0.75f;
    private const float FateTacticalRecloseSlack = 2f;
    private const float FateObstacleMapMeleeProbeMaxDistance = 12f;
    private const int FateCombatReacquireDelayMs = 2000;
    private const float FateCombatReacquireCenterTolerance = 8f;
    private const float FateTravelMountDistance = 30f;
    private const float FateTravelRepathDistance = 8f;
    private const float FateInteractionApproachDistance = 2.25f;
    private const float FateInteractionFallbackDistance = 3.25f;
    private const float FateInteractionArrivalDistance = 0.75f;
    private const float FateLineOfSightRecoveryArrivalDistance = 0.75f;
    private const int FateLineOfSightRecoveryRetryMs = 250;
    private const float FateStagingProjectionDistance = 20f;
    private const float FateInitialEntryRadiusFraction = 0.45f;
    private const float FateEntryAnchorDiscoveryPadding = 25f;
    private const float FateEntryAnchorLandingDistance = 11f;
    private const float FateEntryCombatAnchorClearance = 0.75f;
    private const float FateEntryCombatAnchorProjectionRadius = 3f;
    private const float FateEntryCombatAnchorProjectionPadding = 3f;
    private const float FateEntryAnchorArrivalDistance = 4f;
    private const float FateEntryAnchorInnerMargin = 3f;
    private const int FateDismountRecoveryMs = 3000;
    private const int FateLandingRecoveryMaxAttempts = 4;
    private const float FateLandingRecoveryRadius = 10f;
    private const float FateLocalLayerVerticalTolerance = 8f;
    private const float FateResidualAggroAcquireDistance = 20f;
    private const float FateResidualAggroLeashDistance = 20f;
    private const float FateEscortFollowStartDistance = 14f;
    private const float FateEscortFollowStopDistance = 8f;
    private const int FateEscortFollowRestartDelayMs = 750;
    private const int FateEventObjectCombatGraceMs = 500;
    private const float FateEscortHardLeashDistance = 20f;
    private const float FateEscortCombatLeashDistance = 22f;
    private const float FateEscortIncidentalAggroLeashDistance = 18f;
    private const float FateEscortObstacleMapLocalRadius = 36f;
    private const float FateEscortObstacleMapRecenterDistance = 12f;
    private const float FateEscortRepathDistance = 6f;
    private const float FateDefendReturnStartDistance = 40f;
    private const float FateDefendReturnStopDistance = 28f;
    private const float FateDefendIncidentalAggroLeashDistance = 20f;
    private const float FateDefendRepathDistance = 6f;
    private const float FateDefendObjectiveSwitchHpMargin = 0.02f;
    private const int FateBookCreditWaitSeconds = 10;
    private const int FateZoneTransitionTimeoutSeconds = 75;
    private const int FateTeleportRetrySeconds = 5;
    private const int FateTeleportPostCastGraceSeconds = 5;
    private const int FateTeleportMaxAttempts = 3;
    private const float FateTeleportArrivalValidationRadius = 60f;
    private const int FateZoneProbeSettleMs = 1500;
    private const int FateExperimentalStartTimeoutSeconds = 75;
    private const int FatePreparingDisappearGraceMs = 3000;
    private const float FatePreemptionAggroLeashDistance = 15f;

    private readonly FateAutomationController _fateAutomation = new();
    private FateAutomationContext _fateContext => _fateAutomation.Context;
    private RotationSolverLease _fateRotationSolver => _fateAutomation.RotationSolver;
    private BossModTacticalMovementLease _fateTacticalMovement => _fateAutomation.TacticalMovement;
    private FateTerrainRecoveryController _fateTerrainRecovery => _fateAutomation.TerrainRecovery;
    private FateObstacleMapController _fateObstacleMaps => _fateAutomation.ObstacleMaps;
    private FatePreemptionEgressController _fatePreemptionEgress => _fateAutomation.PreemptionEgress;
    private FateCombatTargetSelector _fateCombatTargets => _fateAutomation.CombatTargets;
    private FateCombatEngagementController _fateCombatEngagement => _fateAutomation.CombatEngagement;

    internal FateAutomationSnapshot GetFateAutomationSnapshot()
        => _fateAutomation.Snapshot;

    private void ReleaseFateTacticalMovement()
    {
        _fateTerrainRecovery.ResetForTacticalRelease();
        _fateTacticalMovement.Release();
    }

    internal IReadOnlyList<FateObjectiveDefinition> GetDebugFateTargets()
        => BraveBook.GetAllFateTargets();

    internal bool StartDebugFate(uint fateId)
    {
        if (_bookState.AutomationState == BookAutomationState.Running)
        {
            ZodiacBuddyPlugin.PrintError("Stop book automation before starting a FATE test.");
            return false;
        }

        if (!_fateAutomation.IsTerminal)
        {
            ZodiacBuddyPlugin.PrintError("Stop the active FATE run before starting another FATE test.");
            return false;
        }

        var target = BraveBook.GetAllFateTargets().FirstOrDefault(candidate => candidate.FateId == fateId);
        if (target.FateId == 0)
            return false;

        StopFateGrindingInternal(true);
        var request = CreateTargetFateRequest(target, FateExecutionPurpose.DebugTarget, false);
        StartFateAutomation(request, null, 0);
        return true;
    }

    internal void CancelFateDebugRun()
    {
        if (_fateGrindingMode != FateGrindingMode.None)
        {
            StopFateGrinding();
            return;
        }

        if (_fateContext.AutomationState != FateAutomationState.Idle)
            CancelActiveRun();
    }

    private static FateExecutionRequest CreateTargetFateRequest(FateObjectiveDefinition target, FateExecutionPurpose purpose, bool probeOnlyWhenAbsent)
        => new(
            checked((ushort)target.FateId),
            target.Name,
            target.ZoneName,
            target.Position.TerritoryType.RowId,
            target.Position,
            purpose,
            probeOnlyWhenAbsent,
            null);

    private void StartFateAutomation(FateObjectiveDefinition target, uint bookId, bool debugMode, bool probeOnlyWhenAbsent = false)
    {
        var purpose = debugMode ? FateExecutionPurpose.DebugTarget : FateExecutionPurpose.RequiredObjective;
        StartFateAutomation(CreateTargetFateRequest(target, purpose, probeOnlyWhenAbsent), debugMode ? null : target, bookId);
    }

    private void StartFateAutomation(FateExecutionRequest request, FateObjectiveDefinition? bookTarget, uint bookId)
    {
        ResetExperimentalGrinderCombatForNewRun();
        ResetRunStateForNewCycle();
        var runId = StartAutomationRun();
        _pathingContext = PathingContext.Fate;
        _fateAutomation.Begin(
            request,
            bookTarget,
            bookId,
            runId,
            request.IsFiller || FateMetadata.GetPrerequisite(request.FateId) == 0);
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Starting {request.Name} ({request.FateId}), purpose={request.Purpose}.");

        if (request.StagingMapLink is { } stagingMap)
            Service.GameGui.OpenMapWithMapLink(stagingMap);
        AttachFateAutomationUpdate();

        if (Service.ClientState.TerritoryType == request.TerritoryTypeId)
        {
            if (request.Purpose == FateExecutionPurpose.GeneralGrinder
                && request.TravelPlan is { } travelPlan
                && travelPlan.UsesTeleport
                && !Service.Configuration.DisableTeleport)
            {
                BeginFateTeleport(
                    travelPlan.BestAetheryteId,
                    travelPlan.BestAetherytePosition,
                    $"Teleporting within {request.ZoneName} for {request.Name}.",
                    $"Teleporting within the current territory for FATE: {request.Name}.");
                return;
            }

            if (request.Purpose == FateExecutionPurpose.GeneralGrinder
                && request.TravelPlan is { } disabledTravelPlan
                && disabledTravelPlan.UsesTeleport
                && Service.Configuration.DisableTeleport)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-TRAVEL] Teleport disabled; using direct travel for {request.FateId}.");
            }

            if (_fateContext.ProbeOnlyWhenAbsent)
                BeginFateZoneProbe();
            else
            {
                _fateContext.AutomationState = FateAutomationState.AwaitingNavmesh;
                _fateContext.AutomationStatus = $"In {request.ZoneName}; waiting for navmesh readiness.";
            }
            return;
        }

        if (request.StagingMapLink is not { } targetMap)
        {
            FinishFateAutomation(FateAutomationState.Unavailable, FateExecutionResultKind.Unavailable, $"{request.Name} is not in the current territory and this execution request has no travel location.");
            return;
        }

        if (Service.Configuration.DisableTeleport)
        {
            FailFateAutomation($"Teleport is disabled and {request.Name} is in {request.ZoneName}.");
            return;
        }

        var aetheryteId = GetNearestAetheryte(targetMap);
        if (aetheryteId == 0)
        {
            FailFateAutomation($"No aetheryte could be resolved for {request.Name} in {request.ZoneName}.");
            return;
        }

        Vector3? destinationPosition = TryResolveFateTeleportDestinationPosition(aetheryteId, out var resolvedDestination)
            ? resolvedDestination
            : null;
        BeginFateTeleport(
            aetheryteId,
            destinationPosition,
            $"Teleporting to {request.ZoneName} for {request.Name}.",
            $"Teleporting to {request.ZoneName} for FATE: {request.Name}.");
    }

    private void BeginFateTeleport(uint aetheryteId, Vector3? destinationPosition, string automationStatus, string stepProgress)
    {
        _fateContext.TeleportAetheryteId = aetheryteId;
        _fateContext.TeleportAttempts = 1;
        _fateContext.TeleportStartTerritoryId = Service.ClientState.TerritoryType;
        _fateContext.TeleportStartPosition = Player.Object?.Position;
        _fateContext.TeleportDestinationPosition = destinationPosition;
        _fateContext.TeleportTransitionObserved = false;
        _fateContext.TeleportScreenNotReadyObserved = false;
        _fateContext.TeleportTerritoryChangeObserved = false;
        _fateContext.TeleportDestinationRecoveryLogged = false;
        _fateContext.TeleportCastObserved = false;
        _fateContext.TeleportCastEndedAt = DateTime.MinValue;
        _fateContext.NextTeleportRetryAt = DateTime.Now.AddSeconds(FateTeleportRetrySeconds);
        _fateContext.AutomationState = FateAutomationState.AwaitingZone;
        _fateContext.AutomationStateDeadline = DateTime.Now.AddSeconds(FateZoneTransitionTimeoutSeconds);
        _fateContext.AutomationStatus = automationStatus;
        Teleport(aetheryteId);
        Service.Plugin.PrintStepProgress(stepProgress);
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-TRAVEL] Teleport requested target={_fateContext.Request.FateId} aetheryte={aetheryteId} attempt=1/{FateTeleportMaxAttempts}.");
    }

    private static bool TryResolveFateTeleportDestinationPosition(uint aetheryteId, out Vector3 position)
    {
        try
        {
            position = ECommons.GameHelpers.Map.AetherytePosition(aetheryteId);
            return true;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning(ex, $"[ZodiacBuddy/FATE-TRAVEL] Could not resolve aetheryte {aetheryteId} position; validating arrival by territory.");
            position = default;
            return false;
        }
    }

    private void AttachFateAutomationUpdate()
    {
        Svc.Framework.Update -= TickFateAutomation;
        Svc.Framework.Update += TickFateAutomation;
    }

    private void StopFateAutomationState(bool cancelled)
    {
        Svc.Framework.Update -= TickFateAutomation;
        StopTeleportAggroCleanup(cancelled ? "FATE automation was cancelled" : "FATE automation stopped");
        ReleaseFateTacticalMovement();
        ReleaseFateRotationSolverNow(cancelled ? "FATE automation was cancelled" : "FATE automation stopped");
        _fateAutomation.Reset(cancelled);
    }

    private void TickFateAutomation(IFramework _)
    {
        ProcessFateRotationSolverStopRetry();
        if (_fateContext.AutomationRunId == 0 || !_automationRun.IsActive(_fateContext.AutomationRunId))
            return;
        if (_fateAutomation.IsTerminal)
            return;

        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            _fatePreemptionEgress.Reset();
            StopFateOwnedNavigation();
            ReleaseFateTacticalMovement();
            RequestFateRotationSolverStop("the player is unconscious");
            _fateContext.AutomationStatus = "Player is unconscious; waiting for recovery before resuming the FATE run.";
            return;
        }

        if (_fateContext.AutomationState == FateAutomationState.AwaitingZone)
        {
            TickFateAwaitingZone();
            return;
        }

        if (Player.Object == null)
            return;

        if (_fateContext.AutomationState == FateAutomationState.Probing)
        {
            TickFateZoneProbe();
            return;
        }

        if (Service.ClientState.TerritoryType != _fateContext.Request.TerritoryTypeId)
        {
            FailFateAutomation($"Left {_fateContext.Request.ZoneName} while the FATE run was active.");
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || !GenericHelpers.IsScreenReady())
            return;

        if (_fateContext.AutomationState == FateAutomationState.AwaitingNavmesh)
        {
            TickFateAwaitingNavmesh();
            return;
        }

        if (_fateContext.AutomationState is FateAutomationState.PreemptionUnsync or FateAutomationState.PreemptionEgress)
        {
            TickFatePreemption();
            return;
        }

        if (_fateContext.AutomationState == FateAutomationState.ClearingAggro)
        {
            TickFateClearingAggro();
            return;
        }

        if (_fateContext.AutomationState == FateAutomationState.AwaitingExternalConfirmation)
        {
            TickFateAwaitingExternalConfirmation();
            return;
        }

        TickFateResolver();
    }

    private void TickFateAwaitingZone()
    {
        ObserveFateTeleportSignals();

        var betweenAreas = Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];
        var screenReady = GenericHelpers.IsScreenReady();
        var destinationValidated = IsFateTeleportDestinationValidated(out var destinationDistance);
        if (destinationValidated && !betweenAreas && screenReady)
        {
            if (!_fateContext.TeleportTransitionObserved && !_fateContext.TeleportDestinationRecoveryLogged)
            {
                _fateContext.TeleportDestinationRecoveryLogged = true;
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-TRAVEL] Arrival recovered without transition signal target={_fateContext.Request.FateId} distance={FormatTeleportDistance(destinationDistance)}.");
            }

            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-TRAVEL] Arrival validated target={_fateContext.Request.FateId} attempts={_fateContext.TeleportAttempts} transition={_fateContext.TeleportTransitionObserved} distance={FormatTeleportDistance(destinationDistance)}.");
            StopTeleportAggroCleanup("FATE teleport arrival validated");
            if (_fateContext.ProbeOnlyWhenAbsent)
                BeginFateZoneProbe();
            else
            {
                _fateContext.AutomationState = FateAutomationState.AwaitingNavmesh;
                _fateContext.AutomationStatus = $"Arrived for {_fateContext.Request.Name}; waiting for navmesh readiness.";
            }
            return;
        }

        if (betweenAreas || !screenReady)
            return;
        if (Player.Object == null)
            return;

        if (TickTeleportAggroCleanup())
        {
            _fateContext.AutomationStateDeadline = DateTime.Now.AddSeconds(FateZoneTransitionTimeoutSeconds);
            _fateContext.AutomationStatus = "Teleport interrupted by combat; clearing the attacker before retrying.";
            return;
        }

        var teleportCasting = Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Casting87];
        if (teleportCasting)
        {
            _fateContext.TeleportCastObserved = true;
            _fateContext.TeleportCastEndedAt = DateTime.MinValue;
        }
        else if (_fateContext.TeleportCastObserved && _fateContext.TeleportCastEndedAt == DateTime.MinValue)
        {
            _fateContext.TeleportCastEndedAt = DateTime.Now;
            _fateContext.NextTeleportRetryAt = _fateContext.TeleportCastEndedAt.AddSeconds(FateTeleportPostCastGraceSeconds);
        }

        if (DateTime.Now >= _fateContext.AutomationStateDeadline)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE-TRAVEL] Teleport timed out target={_fateContext.Request.FateId} territory={Service.ClientState.TerritoryType}/{_fateContext.Request.TerritoryTypeId} attempts={_fateContext.TeleportAttempts} transition={_fateContext.TeleportTransitionObserved} cast={_fateContext.TeleportCastObserved} distance={FormatTeleportDistance(destinationDistance)}.");
            FailFateAutomation($"Timed out waiting for teleport arrival for {_fateContext.Request.Name} after {_fateContext.TeleportAttempts} attempt(s).");
            return;
        }

        if (_fateContext.TeleportAetheryteId == 0
            || _fateContext.TeleportAttempts >= FateTeleportMaxAttempts
            || DateTime.Now < _fateContext.NextTeleportRetryAt
            || Svc.Condition[ConditionFlag.InCombat]
            || Svc.Condition[ConditionFlag.Mounted]
            || !CanAct)
            return;

        var previousCastObserved = _fateContext.TeleportCastObserved;
        var previousCastEndedAt = _fateContext.TeleportCastEndedAt;
        _fateContext.TeleportAttempts++;
        _fateContext.NextTeleportRetryAt = DateTime.Now.AddSeconds(FateTeleportRetrySeconds);
        _fateContext.TeleportCastObserved = false;
        _fateContext.TeleportCastEndedAt = DateTime.MinValue;
        Teleport(_fateContext.TeleportAetheryteId);
        var retryReason = previousCastObserved && previousCastEndedAt != DateTime.MinValue
            ? "cast ended without arrival"
            : _fateContext.TeleportTransitionObserved
                ? "destination did not validate"
                : "no cast or transition observed";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-TRAVEL] Retrying teleport target={_fateContext.Request.FateId} attempt={_fateContext.TeleportAttempts}/{FateTeleportMaxAttempts} reason={retryReason}.");
    }

    private void ObserveFateTeleportSignals()
    {
        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            _fateContext.TeleportTransitionObserved = true;
        if (!GenericHelpers.IsScreenReady())
            _fateContext.TeleportScreenNotReadyObserved = true;
        if (_fateContext.TeleportStartTerritoryId != 0 && Service.ClientState.TerritoryType != _fateContext.TeleportStartTerritoryId)
            _fateContext.TeleportTerritoryChangeObserved = true;
    }

    private bool IsFateTeleportDestinationValidated(out float destinationDistance)
    {
        destinationDistance = float.MaxValue;
        if (Service.ClientState.TerritoryType != _fateContext.Request.TerritoryTypeId)
            return false;

        if (_fateContext.TeleportStartTerritoryId != _fateContext.Request.TerritoryTypeId)
            return true;

        if (_fateContext.TeleportDestinationPosition is not { } destination || Player.Object is not { } player)
            return _fateContext.TeleportTransitionObserved;

        destinationDistance = HorizontalDistance(player.Position, destination);
        return destinationDistance <= FateTeleportArrivalValidationRadius;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }

    private static string FormatTeleportDistance(float distance)
        => distance == float.MaxValue ? "unknown" : $"{distance:F1}y";

    private void BeginFateZoneProbe()
    {
        _fateContext.AutomationState = FateAutomationState.Probing;
        _fateContext.ProbeReadyAt = DateTime.Now.AddMilliseconds(FateZoneProbeSettleMs);
        _fateContext.AutomationStatus = $"Checking {_fateContext.Request.ZoneName} for {_fateContext.Request.Name}.";
    }

    private void TickFateZoneProbe()
    {
        if (DateTime.Now < _fateContext.ProbeReadyAt)
            return;

        var targetId = checked((ushort)_fateContext.Request.FateId);
        if (IsActionableFatePresent(targetId))
        {
            _fateContext.ProbeOnlyWhenAbsent = false;
            _fateContext.AutomationState = FateAutomationState.AwaitingNavmesh;
            _fateContext.AutomationStatus = $"{_fateContext.Request.Name} is active; waiting for navmesh readiness.";
            return;
        }

        var prerequisiteId = FateMetadata.GetPrerequisite(targetId);
        if (prerequisiteId != 0 && !_fateContext.PrerequisiteDone && IsActionableFatePresent(prerequisiteId))
        {
            _fateContext.ProbeOnlyWhenAbsent = false;
            _fateContext.AutomationState = FateAutomationState.AwaitingNavmesh;
            _fateContext.AutomationStatus = $"Prerequisite {FateMetadata.GetName(prerequisiteId)} is active; waiting for navmesh readiness.";
            return;
        }

        FinishFateAutomation(
            FateAutomationState.Unavailable,
            FateExecutionResultKind.Unavailable,
            prerequisiteId == 0
                ? $"{_fateContext.Request.Name} is not currently active in {_fateContext.Request.ZoneName}."
                : $"Neither {_fateContext.Request.Name} nor prerequisite {FateMetadata.GetName(prerequisiteId)} is currently active in {_fateContext.Request.ZoneName}.");
    }

    private static bool IsActionableFatePresent(ushort fateId)
        => TryGetFate(fateId, out var fate)
            && fate.State is DalamudFateState.Running or DalamudFateState.Preparing;

    private bool IsBookFateActionableInCurrentTerritory(FateObjectiveDefinition target)
    {
        if (target.FateId == 0 || Service.ClientState.TerritoryType != target.Position.TerritoryType.RowId)
            return false;

        var targetId = checked((ushort)target.FateId);
        if (IsActionableFatePresent(targetId))
            return true;

        var prerequisiteId = FateMetadata.GetPrerequisite(targetId);
        return prerequisiteId != 0 && IsActionableFatePresent(prerequisiteId);
    }

    private void TickFateAwaitingNavmesh()
    {
        if (_fateContext.NavmeshWaitRequested)
            return;

        _fateContext.NavmeshWaitRequested = true;
        _automationRun.WaitForNavmeshReadiness(
            () =>
            {
                if (!_automationRun.IsActive(_fateContext.AutomationRunId))
                    return;
                _fateContext.NavmeshWaitRequested = false;
                _fateContext.AutomationState = FateAutomationState.Resolving;
                _fateContext.AutomationStatus = $"Resolving {_fateContext.Request.Name}.";
            },
            result => FailFateAutomation($"Navmesh readiness failed for {_fateContext.Request.Name}: {result}."));
    }

    private void CompleteFateAutomation(string status)
        => FinishFateAutomation(FateAutomationState.Completed, FateExecutionResultKind.Completed, status);

    private void RetryFateLater(string status)
        => FinishFateAutomation(FateAutomationState.RetryLater, FateExecutionResultKind.RetryLater, status);

    private void FailFateAutomation(string status)
        => FinishFateAutomation(FateAutomationState.Failed, FateExecutionResultKind.ExecutionFailure, status);

    private void PreemptFateAutomation(string status)
        => FinishFateAutomation(FateAutomationState.Preempted, FateExecutionResultKind.Preempted, status);

    private void FinishFateAutomation(FateAutomationState terminalState, FateExecutionResultKind resultKind, string status)
    {
        var runId = _fateContext.AutomationRunId;
        StopFateOwnedNavigation();
        ReleaseFateTacticalMovement();
        ReleaseFateRotationSolverNow(terminalState switch
        {
            FateAutomationState.Completed => "FATE automation completed",
            FateAutomationState.Unavailable => "FATE probe completed",
            FateAutomationState.RetryLater => "FATE attempt ended and will be retried later",
            FateAutomationState.Preempted => "FATE execution preempted",
            _ => "FATE automation failed",
        });
        _fateAutomation.Finish(terminalState, resultKind, status);
        _pathingContext = PathingContext.None;
        _restartNavigation = null;
        _advancedUnstuck.Cancel();
        StopUnstuckMonitoring();
        if (_automationRun.IsActive(runId))
            _automationRun.Cancel();
        Svc.Framework.Update -= TickFateAutomation;

        if (terminalState == FateAutomationState.Completed)
        {
            Service.Plugin.PrintStepProgress(status);
        }
        else if (terminalState == FateAutomationState.Unavailable)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Probe unavailable: {status}");
        }
        else if ((terminalState is FateAutomationState.RetryLater or FateAutomationState.Preempted)
            || resultKind == FateExecutionResultKind.UnsupportedMechanic)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] {status}");
        }
        else
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/FATE] {status}");
            ZodiacBuddyPlugin.PrintError(status);
        }
    }
}
