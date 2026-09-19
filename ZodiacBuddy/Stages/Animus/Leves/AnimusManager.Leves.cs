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
using ZodiacBuddy.Systems.Combat;
using Callback = ECommons.Automation.Callback;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const uint BeckonEscortLeveTestId = 647;
    private const uint BeckonEscortLeveNpcBaseId = 843;
    private const uint LeveMarkerIconId = 60492;
    private const uint LeveEnemyNameplateIconId = 71244;
    private const float LeveInteractDistance = 4f;
    private const float LeveEnemyApproachDistance = 3.5f;
    private const float LeveMarkerArrivalDistance = 5f;
    private const float LeveMarkerHorizontalArrivalDistance = 3f;
    private const float LeveStartFlightDistance = 35f;
    private const int LeveStartDismountRecoveryMs = 3000;
    private const int LeveStartLandingRecoveryMaxAttempts = 4;
    private const float LeveStartLandingRecoveryRadius = 10f;
    private const int LeveIssuerDismountRecoveryMs = 3000;
    private const int LeveIssuerLandingRecoveryMaxAttempts = 4;
    private const float LeveVisitedMarkerDistance = 12f;
    private const int LeveObjectiveSettleMs = 2000;
    private const int LeveParchmentWaveGraceMs = 4000;
    private const int LeveEventItemUseCooldownMs = 5000;
    private const int LeveEventItemStationarySettleMs = 750;
    private const float LeveObjectiveObjectInteractDistance = 3.5f;
    private const float LeveRoundsDestinationDistance = 3f;
    private const float LeveDefendThreatLeashDistance = 45f;
    private const int LeveMarkerSweepPauseMs = 1200;
    private const int LeveMarkerRetryPauseMs = 250;
    private const int LeveIssuerExitSettleMs = 1200;
    private const int LeveBookCreditWaitSeconds = 10;
    private const float LeveEscortBeckonDistance = 20f;
    private const float LeveEscortLeadDistance = 19f;
    private const float LeveEscortCatchupDistance = 8.5f;
    private const float LeveEscortThreatLeashDistance = 40f;
    private const int LeveEscortBeckonCooldownMs = 2500;
    private const int LeveEscortMovementSampleMs = 200;
    private const int LeveEscortStationarySettleMs = 650;
    private const int LeveEscortFinishSettleMs = 6000;
    private const float LeveEscortFinishApproachDistance = 15f;
    private const float LeveEscortMovementThreshold = 0.15f;
    private static readonly Vector3 BeckonEscortFallbackGoal = new(22.2f, 5f, 389.4f);

    private readonly LeveAutomationController _leveAutomation = new();
    private LeveAutomationContext _leveContext => _leveAutomation.Context;
    private RotationSolverLease _leveRotationSolver => _leveAutomation.RotationSolver;


    internal LeveAutomationSnapshot GetLeveAutomationSnapshot()
        => _leveAutomation.Snapshot;

    internal bool StartDebugLeveAutomation(uint leveId)
    {
        var target = BraveBook.GetAllLeveTargets().FirstOrDefault(candidate => candidate.LeveId == leveId);
        return target.LeveId != 0 && StartLeveAutomation(target, 0, true);
    }

    private unsafe bool StartLeveAutomation(LeveObjectiveDefinition target, uint bookId, bool debugMode, bool allowUnavailableReturn = false, bool rerollMode = false)
    {
        if (!LeveExecutionProfiles.TryGet(target.LeveId, out _))
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/LEVE] No execution profile is registered for LeveId={target.LeveId}.");
            return false;
        }

        Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Starting LeveId={target.LeveId} name='{target.Name}' book={bookId} slot={target.LeveSlot} territory={target.ZoneId} issuer='{target.Issuer}' debug={debugMode} reroll={rerollMode} allowUnavailableReturn={allowUnavailableReturn}.");

        if (!debugMode && !rerollMode)
        {
            if (bookId == 0 || target.LeveSlot is < 0 or > 2)
            {
                Service.PluginLog.Warning($"[ZodiacBuddy/LEVE] Invalid book context for LeveId={target.LeveId}: book={bookId}, slot={target.LeveSlot}.");
                return false;
            }

            var relicNote = RelicNote.Instance();
            if (relicNote == null || relicNote->RelicNoteId != bookId)
            {
                Service.PluginLog.Warning($"[ZodiacBuddy/LEVE] Active book changed before LeveId={target.LeveId} could start.");
                return false;
            }

            if (relicNote->IsLeveComplete(target.LeveSlot))
            {
                Service.Plugin.PrintStepProgress($"{target.Name} is already complete in this book.");
                return false;
            }
        }

        StartLeveTravel(target);
        if (!_automationRun.HasActiveRun)
            return false;

        _leveAutomation.Begin(
            target,
            _automationRun.ActiveRunId,
            bookId,
            debugMode,
            rerollMode,
            allowUnavailableReturn,
            GetLeveIssuerName(target.Issuer));
        Svc.Framework.Update -= ObserveLeveAutomation;
        Svc.Framework.Update += ObserveLeveAutomation;
        return true;
    }

    internal void CancelDebugLeveAutomation()
        => CancelActiveRun();

    private void StopLeveAutomationState(bool stopRsr)
    {
        Svc.Framework.Update -= ObserveLeveAutomation;
        if (stopRsr)
            StopLeveRotationSolver("leve automation stopped");
        _leveAutomation.Reset();
    }

    private unsafe void ObserveLeveAutomation(IFramework _)
    {
        if (_leveAutomation.IsTerminal)
            return;
        if (!_automationRun.IsActive(_leveContext.AutomationRunId))
        {
            StopLeveAutomationState(true);
            return;
        }
        if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.BetweenAreas] || !GenericHelpers.IsScreenReady())
            return;

        if (!_leveContext.DebugMode && _leveContext.BookId != 0)
        {
            var relicNote = RelicNote.Instance();
            if (relicNote != null && relicNote->RelicNoteId != 0 && relicNote->RelicNoteId != _leveContext.BookId)
            {
                FailLeveAutomation($"The active Trials of the Braves book changed while {_leveContext.AutomationTarget.Name} was running.");
                return;
            }
        }

        switch (_leveContext.AutomationState)
        {
            case LeveAutomationState.TravelingToIssuer:
                break;
            case LeveAutomationState.Acquiring:
                DriveLeveAcquisition();
                break;
            case LeveAutomationState.TravelingToStart:
                DriveLeveStartTravel();
                break;
            case LeveAutomationState.Initiating:
                DriveLeveInitiation();
                break;
            case LeveAutomationState.Active:
                DriveActiveLeveProfile();
                break;
            case LeveAutomationState.Returning:
                DriveLeveReturn();
                break;
            case LeveAutomationState.CollectingReward:
                DriveLeveRewardCollection();
                break;
            case LeveAutomationState.AwaitingBookCredit:
                DriveLeveBookCredit();
                break;
            case LeveAutomationState.UnavailableClosing:
                DriveLeveUnavailableClose();
                break;
        }
    }

    private void BeginActiveLeveCombat()
    {
        var profileName = GetLeveExecutionProfileName(_leveContext.AutomationTarget.LeveId);
        _leveAutomation.BeginActive(profileName);
        ResetLeveEscortMotionTracking();
        Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] LeveId={_leveContext.AutomationTarget.LeveId} is BoundByDuty; {profileName} execution started.");
    }

    private void CompleteLeveAutomation()
    {
        StopLeveRotationSolver("reward collected");
        _automationRun.ReplaceNavigation();
        ClearLeveNavigationResult();
        Svc.Framework.Update -= ObserveLeveAutomation;
        _automationRun.Cancel();
        _leveAutomation.Complete();
        Service.Plugin.PrintStepProgress(_leveContext.DebugMode
            ? $"Leve automation test completed for {_leveContext.AutomationTarget.Name}."
            : _leveContext.RerollMode
                ? $"Leve reroll completed with {_leveContext.AutomationTarget.Name}."
                : $"Book credit confirmed for {_leveContext.AutomationTarget.Name}.");
    }

    private void FailLeveAutomation(string message)
    {
        StopLeveRotationSolver("leve automation failed");
        _automationRun.ReplaceNavigation();
        ClearLeveNavigationResult();
        Svc.Framework.Update -= ObserveLeveAutomation;
        _automationRun.Cancel();
        _leveAutomation.Fail(message);
        Service.PluginLog.Warning($"[ZodiacBuddy/LEVE] {message}");
    }
}
