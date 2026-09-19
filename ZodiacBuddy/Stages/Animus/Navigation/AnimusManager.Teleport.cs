using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
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
    private const int TravelTeleportRetrySeconds = 5;
    private const int TravelTeleportPostCastGraceSeconds = 5;
    private const int TravelTeleportMaxAttempts = 3;
    private const int TravelTeleportArrivalTimeoutSeconds = 75;
    private DateTime _travelNextTeleportRetryAt = DateTime.MinValue;
    private DateTime _travelTeleportCastEndedAt = DateTime.MinValue;
    private DateTime _travelTeleportDeadline = DateTime.MinValue;
    private bool _travelTeleportCastObserved;
    private uint _travelTeleportAetheryteId;
    private int _travelTeleportAttempts;
    private bool _teleportAggroCleanupActive;
    private PathingContext _teleportAggroCleanupContext = PathingContext.None;
    private static uint GetNearestAetheryte(MapLinkPayload mapLink) {
        var closestAetheryteId = 0u;
        var closestDistance = double.MaxValue;

        static float ConvertRawPositionToMapCoordinate(int pos, float scale) {
            var c = scale / 100.0f;
            var scaledPos = pos * c / 1000.0f;

            return (41.0f / c * ((scaledPos + 1024.0f) / 2048.0f)) + 1.0f;
        }

        var aetherytes = Service.DataManager.GetExcelSheet<Aetheryte>();
        var mapMarkers = Service.DataManager.GetSubrowExcelSheet<MapMarker>();

        foreach (var aetheryte in aetherytes) {
            if (!aetheryte.IsAetheryte)
                continue;

            if (aetheryte.Territory.Value.RowId != mapLink.TerritoryType.RowId)
                continue;

            var map = aetheryte.Map.Value;
            var scale = map.SizeFactor;
            var name = map.PlaceName.Value.Name.ExtractText();

            var mapMarker = mapMarkers
	            .SelectMany(markers => markers)
	            .FirstOrDefault(m => m.DataType == 3 && m.DataKey.RowId == aetheryte.RowId);
            
            if (mapMarker.RowId is 0) {
                Service.PluginLog.Verbose($"[ZodiacBuddy/NAV] Could not find aetheryte: {name}");
                return 0;
            }

            var aetherX = ConvertRawPositionToMapCoordinate(mapMarker.X, scale);
            var aetherY = ConvertRawPositionToMapCoordinate(mapMarker.Y, scale);

            var distance = Math.Pow(aetherX - mapLink.XCoord, 2) + Math.Pow(aetherY - mapLink.YCoord, 2);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestAetheryteId = aetheryte.RowId;
            }
        }
        return closestAetheryteId;
    }
    private void StartTeleportWaiter()
    {
        _teleportWaitRunId = _automationRun.ActiveRunId;
        if (_teleportWaitRunId == 0)
            return;

        _teleportWaiterActive = true;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Svc.Framework.Update += WaitForBetweenAreasAndExecute;
    }
    private void StopTeleportWaiter()
    {
        StopTeleportAggroCleanup("teleport waiter stopped");
        _teleportWaiterActive = false;
        _teleportWaitRunId = 0;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
    }
    private void ResetTeleportCycleFlags()
    {
        StopTeleportAggroCleanup("teleport cycle reset");
        _enteredBetweenAreas = false;
        _mountTasksQueued = false;
        _teleportWaiterActive = false;
        _travelNextTeleportRetryAt = DateTime.MinValue;
        _travelTeleportCastEndedAt = DateTime.MinValue;
        _travelTeleportDeadline = DateTime.MinValue;
        _travelTeleportCastObserved = false;
        _travelTeleportAetheryteId = 0;
        _travelTeleportAttempts = 0;
    }
    private unsafe void Teleport(uint aetheryteId) {
        if (Player.Object == null) return;
        if (Service.Configuration.DisableTeleport) return;

        Telepo.Instance()->Teleport(aetheryteId, 0);
    }
    private void BeginTravelTeleport(uint aetheryteId)
    {
        _travelTeleportAetheryteId = aetheryteId;
        _travelTeleportAttempts = 1;
        _travelNextTeleportRetryAt = DateTime.Now.AddSeconds(TravelTeleportRetrySeconds);
        _travelTeleportDeadline = DateTime.Now.AddSeconds(TravelTeleportArrivalTimeoutSeconds);
        _travelTeleportCastObserved = false;
        _travelTeleportCastEndedAt = DateTime.MinValue;
        Teleport(aetheryteId);
        Service.PluginLog.Verbose($"{GetTeleportLogPrefix()} Teleporting via aetheryte {aetheryteId} attempt=1/{TravelTeleportMaxAttempts}.");
    }
    private void TickTravelTeleportRetry()
    {
        if (_pathingContext is not (PathingContext.Enemy or PathingContext.Leve) || _enteredBetweenAreas)
            return;

        if (TickTeleportAggroCleanup())
        {
            _travelTeleportDeadline = DateTime.Now.AddSeconds(TravelTeleportArrivalTimeoutSeconds);
            return;
        }

        var teleportCasting = Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Casting87];
        if (teleportCasting)
        {
            if (!_travelTeleportCastObserved)
                Service.PluginLog.Verbose($"{GetTeleportLogPrefix()} Teleport cast observed attempt={_travelTeleportAttempts}/{TravelTeleportMaxAttempts}; suppressing retry while the accepted teleport resolves.");
            _travelTeleportCastObserved = true;
            _travelTeleportCastEndedAt = DateTime.MinValue;
        }
        else if (_travelTeleportCastObserved && _travelTeleportCastEndedAt == DateTime.MinValue)
        {
            _travelTeleportCastEndedAt = DateTime.Now;
            _travelNextTeleportRetryAt = _travelTeleportCastEndedAt.AddSeconds(TravelTeleportPostCastGraceSeconds);
            Service.PluginLog.Verbose($"{GetTeleportLogPrefix()} Teleport cast ended attempt={_travelTeleportAttempts}/{TravelTeleportMaxAttempts}; waiting {TravelTeleportPostCastGraceSeconds}s for the zone transition before allowing another request.");
        }

        if (_travelTeleportAetheryteId == 0
            || _travelTeleportAttempts >= TravelTeleportMaxAttempts
            || DateTime.Now < _travelNextTeleportRetryAt
            || Svc.Condition[ConditionFlag.BetweenAreas]
            || Svc.Condition[ConditionFlag.BetweenAreas51]
            || Svc.Condition[ConditionFlag.Mounted]
            || !GenericHelpers.IsScreenReady()
            || !CanAct)
            return;

        var previousCastObserved = _travelTeleportCastObserved;
        var previousCastEndedAt = _travelTeleportCastEndedAt;
        _travelTeleportAttempts++;
        _travelNextTeleportRetryAt = DateTime.Now.AddSeconds(TravelTeleportRetrySeconds);
        _travelTeleportCastObserved = false;
        _travelTeleportCastEndedAt = DateTime.MinValue;
        Teleport(_travelTeleportAetheryteId);
        var reason = previousCastObserved && previousCastEndedAt != DateTime.MinValue
            ? $"prior cast completed {(DateTime.Now - previousCastEndedAt).TotalSeconds:F1}s ago without a zone transition"
            : "no teleport cast or zone transition was observed";
        Service.PluginLog.Verbose($"{GetTeleportLogPrefix()} Retrying teleport via aetheryte {_travelTeleportAetheryteId} attempt={_travelTeleportAttempts}/{TravelTeleportMaxAttempts}; reason={reason}.");
    }
    private bool TickTeleportAggroCleanup()
    {
        if (!Svc.Condition[ConditionFlag.InCombat])
        {
            if (_teleportAggroCleanupActive)
                StopTeleportAggroCleanup("combat cleared");
            return false;
        }

        if (!_teleportAggroCleanupActive)
        {
            _teleportAggroCleanupActive = true;
            _teleportAggroCleanupContext = _pathingContext;
            Service.PluginLog.Verbose($"{GetTeleportLogPrefix(_teleportAggroCleanupContext)} Teleport is blocked or interrupted by combat; clearing the new attacker before retrying teleport.");
        }

        TargetingHelper.PromoteAggroingEnemy();
        StartTeleportAggroCleanupRotationSolver(_teleportAggroCleanupContext);
        return true;
    }
    private void StopTeleportAggroCleanup(string reason)
    {
        if (!_teleportAggroCleanupActive)
            return;

        var context = _teleportAggroCleanupContext;
        _teleportAggroCleanupActive = false;
        _teleportAggroCleanupContext = PathingContext.None;
        StopTeleportAggroCleanupRotationSolver(context);
        var suffix = reason == "combat cleared" ? "; resuming the teleport lifecycle." : ".";
        Service.PluginLog.Verbose($"{GetTeleportLogPrefix(context)} Teleport-interrupt aggro cleanup ended because {reason}{suffix}");
    }
    private void StartTeleportAggroCleanupRotationSolver(PathingContext context)
    {
        switch (context)
        {
            case PathingContext.Enemy:
                _enemyAutomation.StartTeleportAggroCleanupRotationSolver();
                break;
            case PathingContext.Fate:
                StartFateRotationSolver();
                break;
            case PathingContext.Leve:
                StartLeveRotationSolver();
                break;
        }
    }
    private void StopTeleportAggroCleanupRotationSolver(PathingContext context)
    {
        switch (context)
        {
            case PathingContext.Enemy:
                _enemyAutomation.StopTeleportAggroCleanupRotationSolver();
                break;
            case PathingContext.Fate:
                RequestFateRotationSolverStop("teleport-interrupt aggro cleared");
                break;
            case PathingContext.Leve:
                StopLeveRotationSolver("teleport-interrupt aggro cleared");
                break;
        }
    }
    private string GetTeleportLogPrefix()
        => GetTeleportLogPrefix(_pathingContext);
    private static string GetTeleportLogPrefix(PathingContext context)
        => context switch
        {
            PathingContext.Enemy => "[ZodiacBuddy/ENEMY]",
            PathingContext.Fate => "[ZodiacBuddy/FATE]",
            PathingContext.Leve => "[ZodiacBuddy/LEVE]",
            _ => "[ZodiacBuddy/NAV]",
        };
    internal void WaitForBetweenAreasAndExecute(IFramework framework)
    {
        if (!_automationRun.IsActive(_teleportWaitRunId))
        {
            StopTeleportWaiter();
            return;
        }
        if (!Service.Configuration.IsAtmaManagerEnabled || !_teleportWaiterActive)
            return;

        if (!_enteredBetweenAreas)
        {
            if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            {
                _enteredBetweenAreas = true;
                return;
            }

            TickTravelTeleportRetry();
            if (_travelTeleportDeadline != DateTime.MinValue && DateTime.Now >= _travelTeleportDeadline)
            {
                var message = $"Teleport arrival timed out after {_travelTeleportAttempts} attempt(s); currentTerritory={Service.ClientState.TerritoryType} canAct={CanAct} inCombat={Svc.Condition[ConditionFlag.InCombat]} mounted={Svc.Condition[ConditionFlag.Mounted]} betweenAreas={Svc.Condition[ConditionFlag.BetweenAreas]} betweenAreas51={Svc.Condition[ConditionFlag.BetweenAreas51]} castObserved={_travelTeleportCastObserved} castEndedAt={_travelTeleportCastEndedAt:O}.";
                Service.PluginLog.Warning($"{GetTeleportLogPrefix()} {message}");
                if (_pathingContext == PathingContext.Enemy)
                {
                    _enemyAutomationFailed = true;
                    _automationRun.Cancel();
                }
                else if (_pathingContext == PathingContext.Leve)
                {
                    FailLeveAutomation($"Timed out waiting for teleport to {_leveContext.AutomationTarget.ZoneName} after {_travelTeleportAttempts} attempt(s).");
                }
                StopTeleportWaiter();
            }
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return;
        if (!GenericHelpers.IsScreenReady()) return;
        if (_mountTasksQueued) return;

        _mountTasksQueued = true;

        EnqueueMountUp();
        StopTeleportWaiter();
    }
    private void EnqueueMountUp()
        => _automationRun.WaitForNavmeshReadiness(
            EnqueueMountUpWhenReady,
            HandleTravelNavigationResult);
    private unsafe void EnqueueMountUpWhenReady()
    {
        _automationRun.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Already mounted, skipping mount roulette use.");
                return true;
            }
            var am = ActionManager.Instance();
            const uint rouletteId = 9;
            if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Attempting to use mount roulette...");
                if (am->UseAction(ActionType.GeneralAction, rouletteId))
                {
                    Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Using mount roulette.");
                }
                else
                {
                    Service.PluginLog.Warning("[ZodiacBuddy/NAV] Failed to use mount roulette.");
                }
            }
            else
            {
                Service.PluginLog.Warning("[ZodiacBuddy/NAV] Mount roulette unavailable.");
            }
            return true;
        });
        _automationRun.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Verbose("[ZodiacBuddy/NAV] Skipping wait for mounted because AdvancedUnstuck active.");
                return true;
            }
            return Svc.Condition[ConditionFlag.Mounted];
        });
        _automationRun.Enqueue(() =>
        {
            TryStartMapFlagNavigation(true, HandleTravelNavigationResult);
            _enteredBetweenAreas = false;
            _teleportWaiterActive = false;
            _mountTasksQueued = false;
            return true;
        });
    }
}
