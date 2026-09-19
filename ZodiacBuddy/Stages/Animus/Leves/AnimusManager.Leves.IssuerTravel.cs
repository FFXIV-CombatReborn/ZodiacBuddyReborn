using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXVec3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ZodiacBuddy.Stages.Animus.Data;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    internal IReadOnlyList<LeveObjectiveDefinition> GetDebugLeveTargets()
        => BraveBook.GetAllLeveTargets();
    internal bool StartDebugLeveTravel(uint leveId)
    {
        var target = BraveBook.GetAllLeveTargets().FirstOrDefault(candidate => candidate.LeveId == leveId);
        if (target.LeveId == 0)
            return false;

        Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Debug travel selected LeveId={target.LeveId}, name='{target.Name}', issuer='{target.Issuer}'.");
        StartLeveTravel(target);
        return true;
    }
    private void StartLeveTravel(LeveObjectiveDefinition target)
    {
        ResetRunStateForNewCycle();

        if (Svc.ClientState.TerritoryType == target.ZoneId)
        {
            StartAutomationRun();
            _pathingContext = PathingContext.Leve;
            ResetTeleportCycleFlags();

            var issuer = FindLeveIssuer(target);
            var player = Player.Object;
            if (issuer != null && player != null)
            {
                var distance = Vector3.Distance(player.Position, issuer.Position);
                if (distance <= LeveInteractDistance)
                {
                    Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Already in {target.ZoneName} and {distance:F1}y from issuer '{issuer.Name.TextValue}'; skipping teleport and beginning local acquisition.");
                    CompleteTravelNavigation();
                    return;
                }

                if (distance <= LeveStartFlightDistance)
                {
                    Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Already in {target.ZoneName}; skipping teleport and ground-pathing {distance:F1}y directly to issuer '{issuer.Name.TextValue}'.");
                    _automationRun.WaitForNavmeshReadiness(
                        () => StartSimpleMove(issuer.Position, false, HandleTravelNavigationResult, NavigationPurpose.LeveMapFlagTravel, NavigationRestartPolicy.WalkAfterUnstuck),
                        HandleTravelNavigationResult);
                    return;
                }
            }

            Service.PluginLog.Verbose($"[ZodiacBuddy/LEVE] Already in {target.ZoneName}; skipping teleport and navigating locally to {GetLeveIssuerName(target.Issuer)}.");
            Service.GameGui.OpenMapWithMapLink(target.Position);
            EnqueueMountUp();
            return;
        }

        var aetheryteId = GetNearestAetheryte(target.Position);
        if (aetheryteId == 0)
        {
            Service.PluginLog.Warning($"[ZodiacBuddy/LEVE] Could not find an aetheryte for {target.ZoneName} while preparing LeveId={target.LeveId}.");
            Service.GameGui.OpenMapWithMapLink(target.Position);
            return;
        }

        Service.GameGui.OpenMapWithMapLink(target.Position);
        StartAutomationRun();
        _pathingContext = PathingContext.Leve;
        ResetTeleportCycleFlags();
        BeginTravelTeleport(aetheryteId);
        Service.Plugin.PrintStepProgress($"Teleporting to {target.ZoneName} for Leve: {target.Name}.");
        StartTeleportWaiter();
    }
}
