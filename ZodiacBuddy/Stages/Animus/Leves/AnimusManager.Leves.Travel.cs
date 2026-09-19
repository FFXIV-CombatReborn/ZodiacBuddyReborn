using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using Callback = ECommons.Automation.Callback;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void OnLeveIssuerTravelComplete()
    {
        if (_leveContext.AutomationState != LeveAutomationState.TravelingToIssuer || !_automationRun.IsActive(_leveContext.AutomationRunId))
            return;

        _leveContext.AutomationState = LeveAutomationState.Acquiring;
        _leveContext.AutomationStatus = $"At {GetLeveIssuerName(_leveContext.AutomationTarget.Issuer)}; acquiring exact LeveId={_leveContext.AutomationTarget.LeveId}.";
    }
    private unsafe void DriveLeveStartTravel()
    {
        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out _))
        {
            FailLeveAutomation($"LeveId={_leveContext.AutomationTarget.LeveId} disappeared before initiation.");
            return;
        }

        if (_leveContext.StartAwaitingDismount)
        {
            if (!Svc.Condition[ConditionFlag.Mounted] && !Svc.Condition[ConditionFlag.InFlight])
            {
                _leveContext.StartAwaitingDismount = false;
                _leveContext.StartDismountRequestedAt = DateTime.MinValue;
                _leveContext.AutomationState = LeveAutomationState.Initiating;
                _leveContext.AutomationStatus = "At the leve start marker and dismounted; opening the exact journal entry.";
                return;
            }

            if (_leveContext.StartDismountRequestedAt == DateTime.MinValue
                || (DateTime.Now - _leveContext.StartDismountRequestedAt).TotalMilliseconds < LeveStartDismountRecoveryMs
                || _restartNavigation != null
                || VNavmesh.Path.IsRunning()
                || VNavmesh.Nav.PathfindInProgress())
                return;

            RecoverLeveStartLanding();
            return;
        }

        if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
            return;

        var rawMarker = GetExactLeveMarkers(_leveContext.AutomationTarget).OrderBy(position => Player.Object == null ? 0f : Vector3.Distance(Player.Object.Position, position)).FirstOrDefault();
        if (rawMarker == Vector3.Zero)
        {
            _leveContext.AutomationStatus = "Waiting for the exact leve start marker.";
            return;
        }

        var marker = ResolveLeveStartTravelDestination(rawMarker) ?? rawMarker;
        var player = Player.Object;
        var distance = player == null ? 0f : Vector3.Distance(player.Position, marker);
        var shouldFly = distance >= LeveStartFlightDistance;
        if (shouldFly && !Svc.Condition[ConditionFlag.Mounted])
        {
            if (!EzThrottler.Throttle("ZBR_LeveStartMount", 1000))
                return;

            var actionManager = ActionManager.Instance();
            const uint rouletteId = 9;
            if (actionManager->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0
                && actionManager->UseAction(ActionType.GeneralAction, rouletteId))
            {
                _leveContext.AutomationStatus = $"Start marker is {distance:F1}y away; mounting before flight.";
            }
            return;
        }

        _leveContext.AutomationStatus = $"Moving to exact start marker at ({marker.X:F1}, {marker.Y:F1}, {marker.Z:F1}) by {(shouldFly ? "flight" : "ground")}.";
        _leveContext.FieldNavigationDestination = marker;
        var restartPolicy = shouldFly ? NavigationRestartPolicy.PreserveFlight : NavigationRestartPolicy.WalkAfterUnstuck;
        if (!StartSimpleMove(marker, shouldFly, HandleLeveStartNavigationResult, NavigationPurpose.LeveMapFlagTravel, restartPolicy))
            _leveContext.FieldNavigationDestination = null;
    }
    private Vector3? ResolveLeveStartTravelDestination(Vector3 marker)
    {
        var probe = new Vector3(marker.X, 1024f, marker.Z);
        var resolved = NavigationGeometry.ProjectReachableGround(
            probe,
            8f,
            probe,
            12f,
            2048f,
            out var error);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Start-marker projection failed: {error.Message}");
        return resolved;
    }
    private void HandleLeveStartNavigationResult(AnimusNavigationResult result)
    {
        _leveContext.StartMarker = _leveContext.FieldNavigationDestination ?? Vector3.Zero;
        ClearLeveNavigationResult();
        if (result == AnimusNavigationResult.Cancelled)
            return;
        if (result != AnimusNavigationResult.Arrived)
        {
            _leveContext.AutomationStatus = $"Start-marker navigation ended with {result}; retrying.";
            return;
        }

        if (Svc.Condition[ConditionFlag.Mounted] || Svc.Condition[ConditionFlag.InFlight])
        {
            _leveContext.StartAwaitingDismount = true;
            _leveContext.StartDismountRequestedAt = DateTime.Now;
            _leveContext.AutomationStatus = "At the leve start marker; dismounting before initiation.";
            EnqueueDismount();
            return;
        }

        _leveContext.AutomationState = LeveAutomationState.Initiating;
        _leveContext.AutomationStatus = "At the leve start marker; opening the exact journal entry.";
    }
    private void RecoverLeveStartLanding()
    {
        if (_leveContext.StartLandingRecoveryAttempts >= LeveStartLandingRecoveryMaxAttempts)
        {
            FailLeveAutomation($"Could not find a landable position near the start marker for LeveId={_leveContext.AutomationTarget.LeveId} after {LeveStartLandingRecoveryMaxAttempts} recovery attempts.");
            return;
        }

        var player = Player.Object;
        if (player == null)
            return;

        var attempt = _leveContext.StartLandingRecoveryAttempts++;
        var recovery = ResolveLeveLandingRecoveryPoint(player.Position, _leveContext.StartMarker, attempt);
        if (recovery is not Vector3 destination)
        {
            _leveContext.StartDismountRequestedAt = DateTime.Now;
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Start landing recovery {attempt + 1}/{LeveStartLandingRecoveryMaxAttempts}: no walkable point.");
            return;
        }

        _leveContext.FieldNavigationDestination = destination;
        _leveContext.StartDismountRequestedAt = DateTime.Now;
        _leveContext.AutomationStatus = $"Dismount blocked at start marker; moving to nearby landable ground (recovery {attempt + 1}/{LeveStartLandingRecoveryMaxAttempts}).";
        if (!StartSimpleMove(destination, true, HandleLeveStartLandingRecoveryNavigationResult, NavigationPurpose.LeveMapFlagTravel, NavigationRestartPolicy.PreserveFlight))
            _leveContext.FieldNavigationDestination = null;
    }
    private void HandleLeveStartLandingRecoveryNavigationResult(AnimusNavigationResult result)
    {
        ClearLeveNavigationResult();
        if (result == AnimusNavigationResult.Cancelled)
            return;
        if (result != AnimusNavigationResult.Arrived)
        {
            _leveContext.StartDismountRequestedAt = DateTime.Now.AddMilliseconds(-LeveStartDismountRecoveryMs);
            _leveContext.AutomationStatus = $"Landing recovery ended with {result}; trying another nearby ground point.";
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Start landing recovery {result}; retrying.");
            return;
        }

        _leveContext.StartDismountRequestedAt = DateTime.Now;
        _leveContext.AutomationStatus = "Reached nearby walkable ground; retrying dismount before initiation.";
        EnqueueDismount();
    }
    private static Vector3? ResolveLeveLandingRecoveryPoint(Vector3 playerPosition, Vector3 landingAnchor, int attempt)
    {
        var recovery = LandingRecoveryResolver.ResolveNearbyGround(
            playerPosition,
            landingAnchor,
            attempt,
            LeveStartLandingRecoveryRadius,
            LandingRecoveryPreference.NearestToPlayer,
            out var error);
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Landing recovery-point resolution failed: {error.Message}");
        return recovery;
    }
    private bool RecoverLeveIssuerLanding()
    {
        if (_leveContext.IssuerLandingRecoveryAttempts >= LeveIssuerLandingRecoveryMaxAttempts)
        {
            FailLeveAutomation($"Could not find a landable position near {GetLeveIssuerName(_leveContext.AutomationTarget.Issuer)} for LeveId={_leveContext.AutomationTarget.LeveId} after {LeveIssuerLandingRecoveryMaxAttempts} recovery attempts.");
            return true;
        }

        var player = Player.Object;
        if (player == null)
            return false;

        var issuer = FindLeveIssuer(_leveContext.AutomationTarget);
        var anchor = issuer?.Position ?? ResolveMapFlagDestination() ?? player.Position;
        var attempt = _leveContext.IssuerLandingRecoveryAttempts++;
        var recovery = ResolveLeveLandingRecoveryPoint(player.Position, anchor, attempt);
        if (recovery is not Vector3 destination)
        {
            _leveContext.IssuerDismountRequestedAt = DateTime.Now;
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Issuer landing recovery {attempt + 1}/{LeveIssuerLandingRecoveryMaxAttempts}: no walkable point.");
            return false;
        }

        _leveContext.IssuerDismountRequestedAt = DateTime.Now;
        _leveContext.AutomationStatus = $"Dismount blocked near issuer; moving to nearby landable ground (recovery {attempt + 1}/{LeveIssuerLandingRecoveryMaxAttempts}).";
        if (StartSimpleMove(destination, true, HandleLeveIssuerLandingRecoveryNavigationResult, NavigationPurpose.LeveMapFlagTravel, NavigationRestartPolicy.PreserveFlight))
            return true;

        _leveContext.IssuerDismountRequestedAt = DateTime.Now.AddMilliseconds(-LeveIssuerDismountRecoveryMs);
        return false;
    }
    private void HandleLeveIssuerLandingRecoveryNavigationResult(AnimusNavigationResult result)
    {
        _restartNavigation = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
        if (result == AnimusNavigationResult.Cancelled)
            return;

        if (result != AnimusNavigationResult.Arrived)
        {
            _leveContext.IssuerDismountRequestedAt = DateTime.Now.AddMilliseconds(-LeveIssuerDismountRecoveryMs);
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Issuer landing recovery {result}; retrying.");
        }
        else
        {
            _leveContext.IssuerDismountRequestedAt = DateTime.Now;
        }

        CompleteTravelNavigation();
    }
    private unsafe void DriveLeveInitiation()
    {
        if (Svc.Condition[ConditionFlag.BoundByDuty])
        {
            BeginActiveLeveCombat();
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesNo) && yesNo != null && GenericHelpers.IsAddonReady(yesNo))
        {
            if (EzThrottler.Throttle("ZBR_LeveInitiationYesNo", 700))
            {
                var master = new AddonMaster.SelectYesno(yesNo);
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Confirming initiation for LeveId={_leveContext.AutomationTarget.LeveId}.");
                master.Yes();
                _leveContext.InitiateRequestedAt = DateTime.Now;
            }
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("GuildLeveDifficulty", out var difficulty) && difficulty != null && GenericHelpers.IsAddonReady(difficulty))
        {
            var button = difficulty->GetComponentButtonById(7);
            if (button != null && button->IsEnabled && EzThrottler.Throttle("ZBR_LeveDifficulty", 700))
            {
                button->ClickAddonButton(difficulty);
                _leveContext.InitiateRequestedAt = DateTime.Now;
            }
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("JournalDetail", out var journalDetail) && journalDetail != null && GenericHelpers.IsAddonReady(journalDetail))
        {
            var journal = new AddonMaster.JournalDetail(journalDetail);
            var canRetryInitiate = _leveContext.InitiateRequestedAt == DateTime.MinValue
                || (DateTime.Now - _leveContext.InitiateRequestedAt).TotalSeconds >= 3;
            if (journal.CanInitiate && canRetryInitiate && EzThrottler.Throttle("ZBR_LeveInitiate", 800))
            {
                journal.Initiate();
                _leveContext.InitiateRequestedAt = DateTime.Now;
            }
            return;
        }

        if (_leveContext.InitiateRequestedAt != DateTime.MinValue && (DateTime.Now - _leveContext.InitiateRequestedAt).TotalMilliseconds < 1200)
            return;

        if (EzThrottler.Throttle("ZBR_LeveOpenJournal", 1000))
        {
            AgentQuestJournal.Instance()->OpenForQuest(_leveContext.AutomationTarget.LeveId, 2, keepOpen: true);
        }
    }
    private static unsafe List<Vector3> GetExactLeveMarkers(LeveObjectiveDefinition target)
    {
        var result = new List<Vector3>();
        var hud = AgentHUD.Instance();
        if (hud == null)
            return result;

        foreach (var marker in hud->MapMarkers)
        {
            if (marker.IconId != LeveMarkerIconId)
                continue;
            var tooltip = marker.TooltipString == null ? string.Empty : marker.TooltipString->ToString();
            if (tooltip.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                result.Add(new Vector3(marker.Position.X, marker.Position.Y, marker.Position.Z));
        }
        return result;
    }
    private void EnsureLeveFieldNavigation(Vector3 destination, NavigationPurpose purpose, string reason)
    {
        if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
            return;
        _leveContext.FieldNavigationDestination = destination;
        _leveContext.AutomationStatus = $"Navigating for {reason}.";
        if (!StartSimpleMove(destination, false, HandleLeveObjectiveNavigationResult, purpose, NavigationRestartPolicy.WalkAfterUnstuck))
            _leveContext.FieldNavigationDestination = null;
    }
    private void EnsureLeveProfileNavigation(Vector3 destination, NavigationPurpose purpose, string reason)
    {
        if (_restartNavigation != null || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
        {
            if (_leveContext.FieldNavigationDestination is Vector3 current && Vector3.DistanceSquared(current, destination) <= 4f)
                return;
            _automationRun.ReplaceNavigation();
            ClearLeveNavigationResult();
        }
        EnsureLeveFieldNavigation(destination, purpose, reason);
    }
    private void MarkLeveMarkerVisited(Vector3 marker)
    {
        if (!_leveContext.VisitedMarkers.Any(visited => NavigationGeometry.HorizontalDistanceSquared(visited, marker) < LeveVisitedMarkerDistance * LeveVisitedMarkerDistance))
            _leveContext.VisitedMarkers.Add(marker);
        _leveContext.AutomationStatus = $"Checked marker ({marker.X:F1}, {marker.Y:F1}, {marker.Z:F1}); no live objective here, moving on without waiting for the marker to disappear.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Marker checked; {_leveContext.VisitedMarkers.Count} visited for LeveId={_leveContext.AutomationTarget.LeveId}.");
    }
    private void ClearLeveNavigationResult()
    {
        _restartNavigation = null;
        _leveContext.FieldNavigationDestination = null;
        _unstuckPhase = UnstuckPhase.Idle;
        StopUnstuckMonitoring();
    }
}
