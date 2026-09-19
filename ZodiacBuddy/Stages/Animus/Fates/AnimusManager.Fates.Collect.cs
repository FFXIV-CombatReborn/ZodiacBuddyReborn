using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;
using Callback = ECommons.Automation.Callback;
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const int FateCollectTurnInBatch = 10;

    private unsafe uint GetCollectEventItemId(ushort fateId)
    {
        var sheetEventItem = GetSheetFateEventItemId(fateId);
        if (sheetEventItem != 0)
            return sheetEventItem;

        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        return context == null ? 0u : context->EventItem;
    }

    private int GetCollectEventItemCount(ushort fateId)
        => GetFateEventItemCount(GetCollectEventItemId(fateId));

    private unsafe IGameObject? ResolveCollectObjectiveNpc(ushort fateId)
    {
        var manager = FateManager.Instance();
        var context = manager == null ? null : manager->GetFateById(fateId);
        return context == null ? null : FateTargeting.FindByEntityId(context->ObjectiveNpc);
    }

    private void DriveCollectFate(IFate fate)
    {
        var eventItemId = GetCollectEventItemId(fate.FateId);
        var itemCount = GetFateEventItemCount(eventItemId);
        if (eventItemId == 0)
        {
            _fateContext.CollectTurnInActive = false;
            _fateContext.CollectTurnInSafeAt = DateTime.MinValue;
            _fateContext.AutomationStatus = $"Collect FATE {FateMetadata.GetName(fate.FateId)} exposed no event-item identifier; waiting for discovery data.";
            StopFateOwnedNavigation();
            ReleaseFateTacticalMovement();
            RequestFateRotationSolverStop("Collect FATE has no resolvable event item");
            return;
        }

        if (_fateContext.CollectTurnInActive)
        {
            DriveCollectTurnIn(fate, eventItemId, itemCount);
            return;
        }

        var forceTurnIn = fate.State == DalamudFateState.Ending || fate.Progress >= 90;
        var eventObject = FateTargeting.GetNearestEventObject(fate.FateId);
        var acquisitionTarget = FateTargeting.GetNearestCombatTarget(fate.FateId, _fateContext.CombatTargetId);
        var shouldTurnIn = itemCount > 0
            && (itemCount >= FateCollectTurnInBatch
                || forceTurnIn
                || (eventObject == null && acquisitionTarget == null));

        if (shouldTurnIn)
        {
            DriveCollectTurnIn(fate, eventItemId, itemCount);
            return;
        }

        _fateContext.CollectTurnInActive = false;
        _fateContext.CollectTurnInSafeAt = DateTime.MinValue;
        _fateContext.CollectTurnInStartItemCount = 0;
        _fateContext.CollectRequestFillSlot = 0;
        _fateContext.CollectRequestSubmitAfterFrame = 0;

        if (eventObject != null)
        {
            DriveFateEventObject(fate, eventObject);
            return;
        }

        if (acquisitionTarget != null)
        {
            if (_fateContext.CombatTargetId != acquisitionTarget.GameObjectId)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Collect acquisition target '{acquisitionTarget.Name.TextValue}' id={acquisitionTarget.GameObjectId} fateId={fate.FateId} hp={acquisitionTarget.CurrentHp}/{acquisitionTarget.MaxHp} selected by sticky target then nearest distance; eventItem={eventItemId} carried={itemCount}.");
            DriveFateEnemy(fate, acquisitionTarget);
            return;
        }

        StopFateOwnedNavigation();
        ReleaseFateTacticalMovement();
        RequestFateRotationSolverStop("waiting for a Collect FATE acquisition source");
        _fateContext.AutomationStatus = $"Collect FATE {FateMetadata.GetName(fate.FateId)} has no visible pickup or enemy source; carrying {itemCount}/{FateCollectTurnInBatch}.";
    }

    private unsafe void DriveCollectTurnIn(IFate fate, uint eventItemId, int itemCount)
    {
        if (!_fateContext.CollectTurnInActive)
        {
            _fateContext.CollectTurnInActive = true;
            _fateContext.CollectTurnInSafeAt = DateTime.MinValue;
            _fateContext.CollectTurnInStartItemCount = itemCount;
            _fateContext.CollectRequestFillSlot = 0;
            _fateContext.CollectRequestSubmitAfterFrame = 0;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Collect turn-in started for FateId={fate.FateId}; eventItem={eventItemId} carried={itemCount} handIn={fate.HandInCount} progress={fate.Progress}%.");
        }

        if (itemCount < _fateContext.CollectTurnInStartItemCount)
        {
            if (TryFinishCollectTurnInUi(fate))
                return;

            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Collect turn-in observed inventory decrease for FateId={fate.FateId}; eventItem={eventItemId} {_fateContext.CollectTurnInStartItemCount}->{itemCount}, handIn={fate.HandInCount}, progress={fate.Progress}%.");
            _fateContext.CollectTurnInActive = false;
            _fateContext.CollectTurnInSafeAt = DateTime.MinValue;
            _fateContext.CollectTurnInStartItemCount = 0;
            _fateContext.CollectRequestFillSlot = 0;
            _fateContext.CollectRequestSubmitAfterFrame = 0;
            _fateContext.InteractionTargetId = 0;
            return;
        }

        if (TryDriveCollectTurnInAggroClear(fate))
            return;

        _fateContext.CombatTargetId = 0;
        _fateCombatEngagement.Reset();
        ReleaseFateTacticalMovement();
        RequestFateRotationSolverStop("turning in Collect FATE event items");

        if (TryDriveCollectRequestUi(fate, eventItemId, itemCount))
            return;

        var objectiveNpc = ResolveCollectObjectiveNpc(fate.FateId);
        if (objectiveNpc == null || !objectiveNpc.IsTargetable)
        {
            _fateContext.InteractionTargetId = 0;
            var centerDestination = fate.Position;
            if (fate.State == DalamudFateState.Running
                && NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, centerDestination) > FateCombatReacquireCenterTolerance * FateCombatReacquireCenterTolerance)
                EnsureFateNavigation(centerDestination, false, $"Collect objective NPC reacquire {fate.FateId}", FateNavigationIntent.CollectTurnIn);
            else
                StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Waiting to resolve the Collect turn-in NPC for {FateMetadata.GetName(fate.FateId)}; carrying {itemCount} event item(s).";
            return;
        }

        _fateContext.InteractionTargetId = objectiveNpc.GameObjectId;
        var distance = Vector3.Distance(Player.Object!.Position, objectiveNpc.Position);
        if (distance > FateInteractDistance)
        {
            var approach = ResolveFateInteractionApproach(objectiveNpc.Position) ?? objectiveNpc.Position;
            EnsureFateNavigation(approach, false, $"Collect turn-in NPC {objectiveNpc.GameObjectId}", FateNavigationIntent.CollectTurnIn);
            _fateContext.AutomationStatus = $"Approaching {objectiveNpc.Name.TextValue} to hand in {itemCount} event item(s) for {FateMetadata.GetName(fate.FateId)}.";
            return;
        }

        StopFateOwnedNavigation();
        if (EzThrottler.Throttle($"ZBR_FateCollectTurnInNpc_{fate.FateId}", 900))
        {
            TargetSystem.Instance()->Target = (GameObject*)objectiveNpc.Address;
            TargetSystem.Instance()->InteractWithObject((GameObject*)objectiveNpc.Address, false);
        }
        _fateContext.AutomationStatus = $"Opening the hand-in request at {objectiveNpc.Name.TextValue} for {FateMetadata.GetName(fate.FateId)}; carrying {itemCount} event item(s).";
    }

    private bool TryDriveCollectTurnInAggroClear(IFate fate)
    {
        var attacker = FateTargeting.GetBestAggroTarget(fate.FateId, _fateContext.CombatTargetId, fate.Position, fate.Radius);
        if (attacker != null)
        {
            _fateContext.CollectTurnInSafeAt = DateTime.Now.AddMilliseconds(FateEventObjectCombatGraceMs);
            _fateContext.InteractionTargetId = 0;
            if (_fateContext.CombatTargetId != attacker.GameObjectId)
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Collect turn-in cleanup target '{attacker.Name.TextValue}' id={attacker.GameObjectId} fateId={FateTargeting.GetFateId(attacker)} hp={attacker.CurrentHp}/{attacker.MaxHp}; clearing direct aggro before NPC interaction.");
            DriveFateObjectiveAggro(fate, attacker);
            return true;
        }

        if (DateTime.Now < _fateContext.CollectTurnInSafeAt)
        {
            _fateContext.CombatTargetId = 0;
            _fateCombatEngagement.Reset();
            ReleaseFateTacticalMovement();
            RequestFateRotationSolverStop("waiting for Collect turn-in combat cleanup grace");
            StopFateOwnedNavigation();
            _fateContext.AutomationStatus = $"Waiting briefly for a clean interaction window before turning in items for {FateMetadata.GetName(fate.FateId)}.";
            return true;
        }

        return false;
    }

    private unsafe bool TryFinishCollectTurnInUi(IFate fate)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && talk != null && GenericHelpers.IsAddonReady(talk))
        {
            if (EzThrottler.Throttle($"ZBR_FateCollectTalk_{fate.FateId}", 300))
                new AddonMaster.Talk(talk).Click();
            _fateContext.AutomationStatus = $"Advancing Collect turn-in dialogue for {FateMetadata.GetName(fate.FateId)}.";
            return true;
        }

        if (GenericHelpers.TryGetAddonByName<AddonRequest>("Request", out var request) && request != null && GenericHelpers.IsAddonReady((AtkUnitBase*)request))
        {
            if (EzThrottler.Throttle($"ZBR_FateCollectCloseRequest_{fate.FateId}", 500))
                new AddonMaster.Request((AtkUnitBase*)request).Cancel();
            _fateContext.AutomationStatus = $"Finishing the completed hand-in request for {FateMetadata.GetName(fate.FateId)}.";
            return true;
        }

        return false;
    }

    private unsafe bool TryDriveCollectRequestUi(IFate fate, uint eventItemId, int itemCount)
    {
        var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextIconMenu", 1).Address;
        if (contextMenu != null && contextMenu->IsVisible)
        {
            if (EzThrottler.Throttle($"ZBR_FateCollectContext_{fate.FateId}", 300))
            {
                Callback.Fire(contextMenu, false, 0, 0, 1021003, 0, 0);
                _fateContext.CollectRequestFillSlot++;
                _fateContext.CollectRequestSubmitAfterFrame = Svc.PluginInterface.UiBuilder.FrameCount + 4;
            }
            _fateContext.AutomationStatus = $"Selecting event item {eventItemId} for {FateMetadata.GetName(fate.FateId)} hand-in.";
            return true;
        }

        if (GenericHelpers.TryGetAddonByName<AddonRequest>("Request", out var request) && request != null && GenericHelpers.IsAddonReady((AtkUnitBase*)request))
        {
            if (_fateContext.CollectRequestSubmitAfterFrame == 0)
                _fateContext.CollectRequestSubmitAfterFrame = Svc.PluginInterface.UiBuilder.FrameCount + 4;

            var handOverButton = request->HandOverButton;
            if (handOverButton != null && handOverButton->IsEnabled)
            {
                if (Svc.PluginInterface.UiBuilder.FrameCount < _fateContext.CollectRequestSubmitAfterFrame)
                {
                    _fateContext.AutomationStatus = $"Waiting for the {FateMetadata.GetName(fate.FateId)} hand-in request to settle before submission.";
                    return true;
                }

                if (EzThrottler.Throttle($"ZBR_FateCollectHandOver_{fate.FateId}", 750))
                {
                    Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Collect Request ready for FateId={fate.FateId}; clicking native HandOverButton for eventItem={eventItemId} carried={itemCount} entries={request->EntryCount}.");
                    handOverButton->ClickAddonButton(&request->AtkUnitBase);
                }
                _fateContext.AutomationStatus = $"Handing over {itemCount} event item(s) for {FateMetadata.GetName(fate.FateId)}.";
                return true;
            }

            var entryCount = Math.Max(1, request->EntryCount);
            var slot = Math.Clamp(_fateContext.CollectRequestFillSlot, 0, entryCount - 1);
            if (EzThrottler.Throttle($"ZBR_FateCollectRequest_{fate.FateId}_{slot}", 500))
                Callback.Fire(&request->AtkUnitBase, false, 2, slot, 0, 0);
            _fateContext.AutomationStatus = $"Filling the {FateMetadata.GetName(fate.FateId)} hand-in request with event item {eventItemId}.";
            return true;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && talk != null && GenericHelpers.IsAddonReady(talk))
        {
            if (EzThrottler.Throttle($"ZBR_FateCollectTalk_{fate.FateId}", 300))
                new AddonMaster.Talk(talk).Click();
            _fateContext.AutomationStatus = $"Advancing Collect turn-in dialogue for {FateMetadata.GetName(fate.FateId)}.";
            return true;
        }

        return false;
    }
}
