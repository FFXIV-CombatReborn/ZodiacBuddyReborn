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
    private bool TryBeginLeveReturn(FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions.LeveWork work)
    {
        if (work.Sequence != byte.MaxValue)
            return false;

        _automationRun.ReplaceNavigation();
        ClearLeveNavigationResult();
        StopLeveRotationSolver("field objective complete");
        _leveContext.CombatTargetId = 0;
        _leveContext.AutomationState = LeveAutomationState.Returning;
        _leveContext.AutomationStatus = $"Field objective complete (sequence={work.Sequence}, flags={work.Flags}); waiting for the free return prompt.";
        Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Field objective complete for LeveId={work.LeveId}: sequence={work.Sequence}, flags={work.Flags}, clearClass={work.ClearClass}.");
        return true;
    }
    private unsafe void DriveLeveReturn()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesNo) && yesNo != null && GenericHelpers.IsAddonReady(yesNo))
        {
            if (EzThrottler.Throttle("ZBR_LeveReturnYesNo", 800))
            {
                var master = new AddonMaster.SelectYesno(yesNo);
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Completion SelectYesno prompt='{master.Text}'. Choosing Yes for the game-provided return to issuer.");
                master.Yes();
            }
            return;
        }

        if (!Svc.Condition[ConditionFlag.BoundByDuty])
        {
            var issuer = FindLeveIssuer(_leveContext.AutomationTarget);
            if (issuer != null && Player.Object != null && Vector3.Distance(Player.Object.Position, issuer.Position) <= 10f)
            {
                _leveContext.AutomationState = LeveAutomationState.CollectingReward;
                _leveContext.AutomationStatus = "Returned to issuer; collecting the leve reward.";
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Returned to {issuer.Name.TextValue}; beginning reward collection for LeveId={_leveContext.AutomationTarget.LeveId}.");
                return;
            }

            _leveContext.AutomationStatus = "Leve duty ended, but the issuer is not nearby; waiting for the free return flow rather than navigating manually.";
        }
    }
    private unsafe void DriveLeveRewardCollection()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("JournalResult", out var journalResult) && journalResult != null && GenericHelpers.IsAddonReady(journalResult))
        {
            if (EzThrottler.Throttle("ZBR_LeveJournalResult", 700))
            {
                Callback.Fire(journalResult, true, 0, 0);
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Accepted JournalResult for LeveId={_leveContext.AutomationTarget.LeveId}.");
            }
            return;
        }

        if (!IsLeveAccepted(_leveContext.AutomationTarget.LeveId, out _))
        {
            if (_leveContext.RewardCompletedAt == DateTime.MinValue)
            {
                _leveContext.RewardCompletedAt = DateTime.Now;
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Reward registered for LeveId={_leveContext.AutomationTarget.LeveId}; unwinding the issuer menu before completing automation.");
            }

            if (TryExitLeveIssuerUi("after reward collection"))
                return;
            if ((DateTime.Now - _leveContext.RewardCompletedAt).TotalMilliseconds < LeveIssuerExitSettleMs)
                return;

            if (_leveContext.DebugMode || _leveContext.RerollMode)
            {
                CompleteLeveAutomation();
                return;
            }

            _leveContext.AutomationState = LeveAutomationState.AwaitingBookCredit;
            _leveContext.CreditDeadline = DateTime.Now.AddSeconds(LeveBookCreditWaitSeconds);
            _leveContext.AutomationStatus = $"Reward collected; waiting for book leve slot {_leveContext.AutomationTarget.LeveSlot + 1} to credit.";
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesNo) && yesNo != null && GenericHelpers.IsAddonReady(yesNo))
        {
            if (EzThrottler.Throttle("ZBR_LeveRewardYesNo", 700))
            {
                var master = new AddonMaster.SelectYesno(yesNo);
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Reward SelectYesno prompt='{master.Text}'. Choosing Yes.");
                master.Yes();
            }
            return;
        }

        if (TryDriveLeveTalk())
            return;

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var selectString) && selectString != null && GenericHelpers.IsAddonReady(selectString))
        {
            var master = new AddonMaster.SelectString(selectString);
            if (master.Entries.Length > 0 && EzThrottler.Throttle("ZBR_LeveCollectReward", 700))
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Selecting reward menu index 0 '{master.Entries[0].Text}'.");
                master.Entries[0].Select();
            }
            return;
        }

        var issuer = FindLeveIssuer(_leveContext.AutomationTarget);
        if (issuer == null || Player.Object == null)
        {
            _leveContext.AutomationStatus = "Waiting for the issuer to become available after return.";
            return;
        }

        var distance = Vector3.Distance(Player.Object.Position, issuer.Position);
        if (distance > LeveInteractDistance)
        {
            _leveContext.AutomationStatus = $"Returned {distance:F1}y from issuer; waiting rather than starting a fallback return path.";
            return;
        }

        if (EzThrottler.Throttle("ZBR_LeveRewardIssuerInteract", 1200))
        {
            TargetSystem.Instance()->Target = (GameObject*)issuer.Address;
            TargetSystem.Instance()->InteractWithObject((GameObject*)issuer.Address, false);
            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Interacted with '{issuer.Name.TextValue}' to collect the reward for LeveId={_leveContext.AutomationTarget.LeveId}.");
        }
    }
    private unsafe void DriveLeveBookCredit()
    {
        if (IsActiveBookLeveComplete())
        {
            CompleteLeveAutomation();
            return;
        }

        if (DateTime.Now < _leveContext.CreditDeadline)
            return;

        var relicNote = RelicNote.Instance();
        var activeBook = relicNote == null ? 0u : relicNote->RelicNoteId;
        if (activeBook != _leveContext.BookId)
        {
            FailLeveAutomation($"The active book changed before {_leveContext.AutomationTarget.Name} could receive leve credit.");
            return;
        }

        FailLeveAutomation($"{_leveContext.AutomationTarget.Name} finished, but book leve slot {_leveContext.AutomationTarget.LeveSlot + 1} did not credit within {LeveBookCreditWaitSeconds} seconds.");
    }
    private unsafe bool IsActiveBookLeveComplete()
    {
        if (_leveContext.DebugMode || _leveContext.BookId == 0 || _leveContext.AutomationTarget.LeveSlot is < 0 or > 2)
            return false;

        var relicNote = RelicNote.Instance();
        return relicNote != null
            && relicNote->RelicNoteId == _leveContext.BookId
            && relicNote->IsLeveComplete(_leveContext.AutomationTarget.LeveSlot);
    }
}
