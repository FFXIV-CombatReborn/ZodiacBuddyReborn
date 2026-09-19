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
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void StageForAbsentFate(ushort fateId, bool prerequisite)
    {
        if (_fateContext.Request.IsFiller)
        {
            FinishFateAutomation(FateAutomationState.Unavailable, FateExecutionResultKind.Unavailable, $"Filler FATE {FateMetadata.GetName(fateId)} ({fateId}) is no longer active.");
            return;
        }

        if (ResolveFateStagingMapLink(fateId) is not { } stagingMap)
        {
            FailFateAutomation($"No staging location is available for {FateMetadata.GetName(fateId)} ({fateId}).");
            return;
        }

        var destination = ResolveStagingMapDestination(stagingMap);
        if (destination is not Vector3 stagingDestination)
        {
            _fateContext.AutomationStatus = $"Waiting for {FateMetadata.GetName(fateId)}; staging point is not yet available on navmesh.";
            return;
        }

        _fateContext.StagingDestination = stagingDestination;
        SetWorkingFate(fateId, prerequisite);
        var player = Player.Object!;
        if (NavigationGeometry.HorizontalDistanceSquared(player.Position, stagingDestination) > 8f * 8f)
        {
            _fateContext.AutomationStatus = prerequisite
                ? $"Staging for prerequisite {FateMetadata.GetName(fateId)} ({fateId})."
                : $"Staging for {_fateContext.Request.Name} ({fateId}).";
            EnsureFateNavigation(stagingDestination, true, prerequisite ? $"prerequisite staging {fateId}" : $"target staging {fateId}", FateNavigationIntent.Staging);
            return;
        }

        var fallbackSpawner = FateMetadata.GetFallbackSpawnerName(fateId);
        if (fallbackSpawner != null)
        {
            DriveAbsentFateSpawner(fateId, stagingDestination, fallbackSpawner, prerequisite);
            return;
        }

        StopFateOwnedNavigation();
        RequestFateRotationSolverStop("waiting for the selected FATE to spawn");
        _fateContext.AutomationState = FateAutomationState.Resolving;
        _fateContext.AutomationStatus = prerequisite
            ? $"Waiting at the prerequisite spawn for {FateMetadata.GetName(fateId)} ({fateId})."
            : $"Waiting at the spawn for {_fateContext.Request.Name} ({fateId}).";
    }
    private void DriveFateStart(IFate fate, bool prerequisite)
    {
        var stagingMap = ResolveFateStagingMapLink(fate.FateId);
        var staging = fate.Position != Vector3.Zero
            ? ResolveReachableFateDestination(fate.Position, fate.Radius, fate.Position.Y) ?? fate.Position
            : stagingMap is { } map
                ? ResolveStagingMapDestination(map) ?? Player.Object!.Position
                : Player.Object!.Position;
        var fallbackSpawner = FateMetadata.GetFallbackSpawnerName(fate.FateId);
        var npc = FateTargeting.FindStartNpc(fate.FateId, staging, fallbackSpawner);
        if (npc == null)
        {
            if (fallbackSpawner != null && EzThrottler.Throttle($"ZBR_FateStarterDiag_{fate.FateId}", 3000))
                FateTargeting.LogStartNpcCandidates(fate.FateId, staging, fallbackSpawner);

            if (NavigationGeometry.HorizontalDistanceSquared(Player.Object!.Position, staging) > 8f * 8f)
                EnsureFateNavigation(staging, true, $"FATE starter staging {fate.FateId}", FateNavigationIntent.Staging);
            else
                _fateContext.AutomationStatus = $"{FateMetadata.GetName(fate.FateId)} is preparing; waiting for its start NPC.";
            return;
        }

        DriveFateStarterObject(fate.FateId, npc, prerequisite);
    }
    private void DriveAbsentFateSpawner(ushort fateId, Vector3 staging, string fallbackName, bool prerequisite)
    {
        var npc = FateTargeting.FindStartNpc(fateId, staging, fallbackName);
        if (npc == null)
        {
            _fateContext.AutomationStatus = $"Waiting for {fallbackName} to become available for {FateMetadata.GetName(fateId)}.";
            return;
        }

        DriveFateStarterObject(fateId, npc, prerequisite);
    }
    private unsafe void DriveFateStarterObject(ushort fateId, IGameObject npc, bool prerequisite)
    {
        RequestFateRotationSolverStop("interacting with a FATE start NPC");
        _fateContext.AutomationState = FateAutomationState.StartingNpc;
        if (IsEscortFate(fateId))
            PinFateEscortAnchor(fateId, npc);
        else if (IsDefendFate(fateId))
            PinFateDefendAnchor(fateId, npc);
        var player = Player.Object!;
        if (_fateContext.StarterObjectId != npc.GameObjectId)
        {
            _fateContext.StarterObjectId = npc.GameObjectId;
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Selected starter object FateId={fateId} name='{npc.Name.TextValue}' kind={npc.ObjectKind} entityId={npc.EntityId} gameObjectId={npc.GameObjectId} icon={FateTargeting.GetNameplateIconId(npc)} objectFateId={FateTargeting.GetFateId(npc)} position={npc.Position} distance={Vector3.Distance(player.Position, npc.Position):F1}y.");
        }
        if (Vector3.Distance(player.Position, npc.Position) > FateInteractDistance)
        {
            _fateContext.InteractionTargetId = npc.GameObjectId;
            var approach = ResolveFateInteractionApproach(npc.Position) ?? npc.Position;
            EnsureFateNavigation(approach, true, $"FATE starter {fateId}", FateNavigationIntent.StarterNpc);
            _fateContext.AutomationStatus = $"Approaching {npc.Name.TextValue} to start {FateMetadata.GetName(fateId)}.";
            return;
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            QueueFateDismount();
            _fateContext.AutomationStatus = $"Landing to talk to {npc.Name.TextValue}.";
            return;
        }

        StopFateOwnedNavigation();
        if (TryGetFate(fateId, out var live) && live.State == DalamudFateState.Running)
        {
            _fateContext.AutomationState = FateAutomationState.Resolving;
            return;
        }

        if (EzThrottler.Throttle($"ZBR_FateInteract_{fateId}", 1200))
        {
            TargetSystem.Instance()->Target = (GameObject*)npc.Address;
            TargetSystem.Instance()->InteractWithObject((GameObject*)npc.Address, false);
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Interacted with '{npc.Name.TextValue}' while starting FATE {fateId}.");
        }

        DriveFateInteractionAddons(fateId);
        _fateContext.AutomationStatus = prerequisite
            ? $"Starting prerequisite {FateMetadata.GetName(fateId)} via {npc.Name.TextValue}."
            : $"Starting {FateMetadata.GetName(fateId)} via {npc.Name.TextValue}.";
    }
    private unsafe void DriveFateInteractionAddons(ushort fateId)
    {
        if (TryGetFate(fateId, out var live) && live.State == DalamudFateState.Running)
            return;

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && talk != null && talk->IsVisible
            && EzThrottler.Throttle($"ZBR_FateTalk_{fateId}", 400))
        {
            new AddonMaster.Talk(talk).Click();
            return;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesNo) && yesNo != null && yesNo->IsVisible
            && EzThrottler.Throttle($"ZBR_FateYesNo_{fateId}", 600))
        {
            try
            {
                new AddonMaster.SelectYesno(yesNo).Yes();
            }
            catch (Exception exception)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] SelectYesno changed while starting FateId={fateId}; retrying on a later tick. {exception.Message}");
            }
        }
    }
    private MapLinkPayload? ResolveFateStagingMapLink(ushort fateId)
    {
        if (fateId == _fateContext.Request.FateId && _fateContext.Request.StagingMapLink is { } requestMap)
            return requestMap;

        if (_fateContext.HasBookTarget)
            return FateMetadata.GetStagingMapLink(fateId, _fateContext.BookTarget);

        return null;
    }

    private Vector3? ResolveStagingMapDestination(MapLinkPayload mapLink)
    {
        var raw = new Vector3(mapLink.RawX / 1000f, 1024f, mapLink.RawY / 1000f);
        return ResolveReachableFateDestination(raw, FateStagingProjectionDistance);
    }
    private static Vector3? ResolveFateInteractionApproach(Vector3 position)
    {
        var floorProbe = new Vector3(position.X, position.Y + 3f, position.Z);
        var resolved = NavigationGeometry.ProjectReachableGround(
            floorProbe,
            FateInteractionApproachDistance,
            position,
            FateInteractionApproachDistance,
            FateLocalLayerVerticalTolerance,
            out var error,
            position.Y,
            FateLocalLayerVerticalTolerance,
            position,
            FateInteractionApproachDistance);
        if (resolved == null && error == null)
        {
            resolved = NavigationGeometry.ProjectReachableGround(
                floorProbe,
                FateInteractionFallbackDistance,
                position,
                FateInteractionFallbackDistance,
                FateLocalLayerVerticalTolerance,
                out error,
                position.Y,
                FateLocalLayerVerticalTolerance,
                position,
                FateInteractionFallbackDistance);
        }
        if (error != null)
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE] Could not project FATE interaction position {position} onto reachable navmesh: {error.Message}");
        return resolved;
    }
}
