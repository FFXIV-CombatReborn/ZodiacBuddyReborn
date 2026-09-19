using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;
using DalamudFateState = Dalamud.Game.ClientState.Fates.FateState;

namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private const int ExperimentalFillerBackoffSeconds = 30;
    private const uint TwistOfFateStatusId = 1288;

    private FateGrindingMode _fateGrindingMode;
    private ushort _fateGrindingDesiredFateId;
    private ushort _fateGrindingActiveFillerId;
    private int _fateGrindingAttempts;
    private string _fateGrindingStatus = "Idle.";
    private FateExecutionResult _fateGrindingLastResult = FateExecutionResult.None;
    private readonly Dictionary<ushort, DateTime> _fateGrindingBackoffUntil = [];
    private readonly FateObservationTracker _fateObservationTracker = new();

    internal FateGrindingSnapshot GetFateGrindingSnapshot()
        => new(
            _fateGrindingMode,
            _fateGrindingMode != FateGrindingMode.None,
            _fateGrindingDesiredFateId,
            _fateGrindingActiveFillerId,
            _fateGrindingStatus,
            _fateGrindingAttempts,
            _fateGrindingLastResult);

    internal unsafe IReadOnlyList<FateRuntimeDiagnostic> GetLiveFateDiagnostics()
    {
        var player = Player.Object;
        var currentFateId = GetCurrentFateId();
        var liveFates = Svc.Fates.Where(fate => fate.FateId != 0).ToArray();
        var liveIds = liveFates.Select(fate => fate.FateId).ToHashSet();
        var bookTargets = BraveBook.GetAllFateTargets();
        var bookTargetIds = bookTargets.Where(target => target.FateId != 0).Select(target => checked((ushort)target.FateId)).ToHashSet();
        var prerequisiteIds = bookTargets
            .Where(target => target.FateId != 0)
            .Select(target => FateMetadata.GetPrerequisite(checked((ushort)target.FateId)))
            .Where(fateId => fateId != 0)
            .ToHashSet();
        var taggedEnemies = liveIds.ToDictionary(fateId => fateId, _ => 0);
        var taggedFriendlies = liveIds.ToDictionary(fateId => fateId, _ => 0);
        var taggedEventObjects = liveIds.ToDictionary(fateId => fateId, _ => 0);
        var taggedOtherObjects = liveIds.ToDictionary(fateId => fateId, _ => 0);
        var taggedActors = liveIds.ToDictionary(fateId => fateId, _ => new List<string>());
        var ruleRaw = new Dictionary<ushort, byte>();
        var ruleNames = new Dictionary<ushort, string>();
        var fateRuleEx = new Dictionary<ushort, ushort>();
        var motivationNpcIds = new Dictionary<ushort, uint>();
        var motivationNpcSummaries = new Dictionary<ushort, string>();
        var objectiveNpcIds = new Dictionary<ushort, uint>();
        var objectiveNpcSummaries = new Dictionary<ushort, string>();
        var sheetEventItemIds = new Dictionary<ushort, uint>();
        var sheetEventItemCounts = new Dictionary<ushort, int>();
        var eventItemIds = new Dictionary<ushort, uint>();
        var eventItemCounts = new Dictionary<ushort, int>();
        var requiredEventItemIds = new Dictionary<ushort, uint>();
        var requiredEventItemCounts = new Dictionary<ushort, int>();
        var turnInEventItemIds = new Dictionary<ushort, uint>();
        var turnInEventItemCounts = new Dictionary<ushort, int>();
        var objectiveMarkerCounts = new Dictionary<ushort, int>();
        var objectiveMarkerSummaries = new Dictionary<ushort, string>();
        var motivationOwners = new Dictionary<uint, ushort>();
        var objectiveOwners = new Dictionary<uint, ushort>();
        var manager = FateManager.Instance();

        foreach (var fate in liveFates)
        {
            var context = manager == null ? null : manager->GetFateById(fate.FateId);
            var motivationNpcId = context == null ? 0u : context->MotivationNpc;
            var objectiveNpcId = context == null ? 0u : context->ObjectiveNpc;
            var runtimeRule = context == null ? (byte)0 : context->Rule;
            var runtimeFateRuleEx = context == null ? (ushort)0 : context->FateRuleEx;
            var sheetEventItemId = GetSheetFateEventItemId(fate.FateId);
            var eventItemId = context == null ? 0u : context->EventItem;
            var requiredEventItemId = context == null ? 0u : context->ReqEventItem;
            var turnInEventItemId = context == null ? 0u : context->TurnInEventItem;

            ruleRaw[fate.FateId] = runtimeRule;
            ruleNames[fate.FateId] = GetFateRuleName(runtimeRule);
            fateRuleEx[fate.FateId] = runtimeFateRuleEx;
            motivationNpcIds[fate.FateId] = motivationNpcId;
            motivationNpcSummaries[fate.FateId] = DescribeUnloadedFateObject(motivationNpcId);
            objectiveNpcIds[fate.FateId] = objectiveNpcId;
            objectiveNpcSummaries[fate.FateId] = DescribeUnloadedFateObject(objectiveNpcId);
            sheetEventItemIds[fate.FateId] = sheetEventItemId;
            sheetEventItemCounts[fate.FateId] = GetFateEventItemCount(sheetEventItemId);
            eventItemIds[fate.FateId] = eventItemId;
            eventItemCounts[fate.FateId] = GetFateEventItemCount(eventItemId);
            requiredEventItemIds[fate.FateId] = requiredEventItemId;
            requiredEventItemCounts[fate.FateId] = GetFateEventItemCount(requiredEventItemId);
            turnInEventItemIds[fate.FateId] = turnInEventItemId;
            turnInEventItemCounts[fate.FateId] = GetFateEventItemCount(turnInEventItemId);
            var markerCount = 0;
            objectiveMarkerSummaries[fate.FateId] = context == null
                ? "unavailable"
                : GetFateObjectiveMarkerSummary(context, out markerCount);
            objectiveMarkerCounts[fate.FateId] = markerCount;

            if (motivationNpcId != 0 && motivationNpcId != 0xE0000000)
                motivationOwners[motivationNpcId] = fate.FateId;
            if (objectiveNpcId != 0 && objectiveNpcId != 0xE0000000)
                objectiveOwners[objectiveNpcId] = fate.FateId;
        }

        foreach (var obj in Svc.Objects)
        {
            if (motivationOwners.TryGetValue(obj.EntityId, out var motivationFateId))
                motivationNpcSummaries[motivationFateId] = DescribeLoadedFateObject(obj);
            if (objectiveOwners.TryGetValue(obj.EntityId, out var objectiveFateId))
                objectiveNpcSummaries[objectiveFateId] = DescribeLoadedFateObject(obj);

            var fateId = FateTargeting.GetFateId(obj);
            if (!liveIds.Contains(fateId))
                continue;

            var name = string.IsNullOrWhiteSpace(obj.Name.TextValue) ? "<unnamed>" : obj.Name.TextValue;
            var icon = FateTargeting.GetNameplateIconId(obj);
            if (obj.ObjectKind == ObjectKind.EventObj)
            {
                taggedEventObjects[fateId]++;
                taggedActors[fateId].Add($"EventObj '{name}' entity={obj.EntityId} game={obj.GameObjectId} icon={icon} targetable={obj.IsTargetable} pos={FormatVector(obj.Position)}");
                continue;
            }

            if (obj is IBattleNpc npc)
            {
                var hostile = FateTargeting.IsAttackableEnemy(npc);
                if (hostile)
                    taggedEnemies[fateId]++;
                else
                    taggedFriendlies[fateId]++;
                taggedActors[fateId].Add($"BattleNpc '{name}' entity={obj.EntityId} game={obj.GameObjectId} base={npc.BaseId} nameId={npc.NameId} icon={icon} targetable={obj.IsTargetable} hostile={hostile} pos={FormatVector(obj.Position)}");
                continue;
            }

            taggedOtherObjects[fateId]++;
            taggedActors[fateId].Add($"{obj.ObjectKind} '{name}' entity={obj.EntityId} game={obj.GameObjectId} icon={icon} targetable={obj.IsTargetable} pos={FormatVector(obj.Position)}");
        }

        var result = new List<FateRuntimeDiagnostic>(liveFates.Length);
        foreach (var fate in liveFates)
        {
            var hasKnownSpecialHandling = ruleRaw[fate.FateId] is FateRuleCollect or FateRuleEscort or FateRuleDefend
                || FateMetadata.IsEscort(fate.FateId)
                || FateMetadata.GetPrimaryCombatTargetName(fate.FateId) != null
                || FateMetadata.PrefersEventObjects(fate.FateId)
                || FateMetadata.GetFallbackSpawnerName(fate.FateId) != null;
            var actors = taggedActors[fate.FateId];
            result.Add(new FateRuntimeDiagnostic(
                fate.FateId,
                string.IsNullOrWhiteSpace(fate.Name.TextValue) ? FateMetadata.GetName(fate.FateId) : fate.Name.TextValue,
                fate.Description.TextValue,
                fate.Objective.TextValue,
                fate.State.ToString(),
                (int)fate.Progress,
                (int)fate.HandInCount,
                fate.StartTimeEpoch,
                fate.Duration,
                fate.TimeRemaining,
                fate.HasBonus,
                fate.Level,
                fate.MaxLevel,
                fate.IconId,
                fate.MapIconId,
                fate.TerritoryType.RowId,
                fate.Position,
                fate.Radius,
                player == null || fate.Position == Vector3.Zero ? float.MaxValue : Vector3.Distance(player.Position, fate.Position),
                currentFateId == fate.FateId,
                bookTargetIds.Contains(fate.FateId),
                prerequisiteIds.Contains(fate.FateId),
                hasKnownSpecialHandling,
                taggedEnemies[fate.FateId],
                taggedFriendlies[fate.FateId],
                taggedEventObjects[fate.FateId],
                taggedOtherObjects[fate.FateId],
                actors.Count == 0 ? "none currently loaded" : string.Join(" | ", actors),
                ruleRaw[fate.FateId],
                ruleNames[fate.FateId],
                fateRuleEx[fate.FateId],
                motivationNpcIds[fate.FateId],
                motivationNpcSummaries[fate.FateId],
                objectiveNpcIds[fate.FateId],
                objectiveNpcSummaries[fate.FateId],
                sheetEventItemIds[fate.FateId],
                sheetEventItemCounts[fate.FateId],
                eventItemIds[fate.FateId],
                eventItemCounts[fate.FateId],
                requiredEventItemIds[fate.FateId],
                requiredEventItemCounts[fate.FateId],
                turnInEventItemIds[fate.FateId],
                turnInEventItemCounts[fate.FateId],
                objectiveMarkerCounts[fate.FateId],
                objectiveMarkerSummaries[fate.FateId]));
        }

        return result
            .OrderByDescending(fate => fate.IsCurrentFate)
            .ThenBy(fate => fate.State == DalamudFateState.Running.ToString() ? 0 : 1)
            .ThenBy(fate => fate.Distance)
            .ThenBy(fate => fate.FateId)
            .ToArray();
    }

    private static string GetFateRuleName(byte rule)
        => rule switch
        {
            1 => "Normal",
            2 => "Collect",
            3 => "Escort",
            4 => "Defend",
            5 => "EventFate",
            6 => "Chase",
            7 => "ConcertedWorks",
            8 => "Fete",
            0 => "None",
            _ => $"Unknown({rule})",
        };

    private static string DescribeUnloadedFateObject(uint entityId)
        => entityId == 0 || entityId == 0xE0000000
            ? "none"
            : $"entity={entityId} (not currently loaded)";

    private static string DescribeLoadedFateObject(IGameObject obj)
    {
        var name = string.IsNullOrWhiteSpace(obj.Name.TextValue) ? "<unnamed>" : obj.Name.TextValue;
        return $"{obj.ObjectKind} '{name}' entity={obj.EntityId} game={obj.GameObjectId} icon={FateTargeting.GetNameplateIconId(obj)} targetable={obj.IsTargetable} pos={FormatVector(obj.Position)}";
    }

    private static string FormatVector(Vector3 position)
        => $"({position.X:F1},{position.Y:F1},{position.Z:F1})";

    private static uint GetSheetFateEventItemId(ushort fateId)
    {
        try
        {
            return Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Fate>().GetRow(fateId).EventItem.RowId;
        }
        catch
        {
            return 0;
        }
    }

    private static unsafe int GetFateEventItemCount(uint itemId)
    {
        if (itemId == 0)
            return 0;

        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return 0;

        var keyItems = inventory->GetInventoryContainer(InventoryType.KeyItems);
        if (keyItems == null)
            return 0;

        var count = 0;
        for (var i = 0; i < keyItems->Size; i++)
        {
            var slot = keyItems->GetInventorySlot(i);
            if (slot != null && slot->ItemId == itemId)
                count += (int)slot->Quantity;
        }
        return count;
    }

    private static unsafe string GetFateObjectiveMarkerSummary(FateContext* context, out int count)
    {
        const int objectiveArrayOffset = 0x3D0;
        const int objectiveSize = 0x20;
        const int objectiveSlots = 32;
        count = 0;
        var entries = new List<string>();
        var baseAddress = (byte*)context + objectiveArrayOffset;

        for (var i = 0; i < objectiveSlots; i++)
        {
            var entry = baseAddress + (i * objectiveSize);
            var iconId = *(uint*)(entry + 0x00);
            var targetMarkerLayoutId = *(uint*)(entry + 0x04);
            var position = *(Vector3*)(entry + 0x10);
            var flags = *(uint*)(entry + 0x1C);
            if (iconId == 0 && targetMarkerLayoutId == 0 && flags == 0 && position == Vector3.Zero)
                continue;

            count++;
            entries.Add($"#{i} icon={iconId} targetMarkerLayout={targetMarkerLayoutId} pos={FormatVector(position)} flags=0x{flags:X8}");
        }

        return entries.Count == 0 ? "none" : string.Join(" | ", entries);
    }

    internal bool StartDebugFateWithGrinding(uint fateId)
    {
        if (_bookState.AutomationState == BookAutomationState.Running)
        {
            ZodiacBuddyPlugin.PrintError("Stop book automation before using the FATE test grinder.");
            return false;
        }

        if (!_fateAutomation.IsTerminal)
        {
            ZodiacBuddyPlugin.PrintError("Stop the active FATE run before starting a selected-target grind test.");
            return false;
        }

        var target = BraveBook.GetAllFateTargets().FirstOrDefault(candidate => candidate.FateId == fateId);
        if (target.FateId == 0)
            return false;

        StopFateGrindingInternal(true);
        _fateGrindingMode = FateGrindingMode.WaitForSelectedTarget;
        _fateGrindingDesiredFateId = checked((ushort)target.FateId);
        _fateGrindingActiveFillerId = 0;
        _fateGrindingAttempts = 0;
        _fateGrindingLastResult = FateExecutionResult.None;
        _fateGrindingBackoffUntil.Clear();
        _fateGrindingStatus = $"Checking {target.ZoneName} for {target.Name}; filler grinding will begin if it is absent.";
        Svc.Framework.Update -= TickFateGrinding;
        Svc.Framework.Update += TickFateGrinding;
        StartFateAutomation(CreateTargetFateRequest(target, FateExecutionPurpose.DebugTarget, true), null, 0);
        return true;
    }

    internal bool StartGeneralFateGrinding()
    {
        if (_bookState.AutomationState == BookAutomationState.Running)
        {
            ZodiacBuddyPlugin.PrintError("Stop book automation before using the general FATE grinder.");
            return false;
        }

        if (!_fateAutomation.IsTerminal)
        {
            ZodiacBuddyPlugin.PrintError("Stop the active FATE run before starting the general FATE grinder.");
            return false;
        }

        StopFateGrindingInternal(true);
        _fateGrindingMode = FateGrindingMode.General;
        _fateGrindingDesiredFateId = 0;
        _fateGrindingActiveFillerId = 0;
        _fateGrindingAttempts = 0;
        _fateGrindingLastResult = FateExecutionResult.None;
        _fateGrindingBackoffUntil.Clear();
        _fateObservationTracker.Reset();
        _fateGrindingStatus = "General FATE grinding enabled for the current territory.";
        Svc.Framework.Update -= TickFateGrinding;
        Svc.Framework.Update += TickFateGrinding;
        TickFateGrinding(Svc.Framework);
        return true;
    }

    internal bool StartDebugLiveFate(uint fateId)
    {
        if (_bookState.AutomationState == BookAutomationState.Running)
        {
            ZodiacBuddyPlugin.PrintError("Stop book automation before running a live FATE from the test harness.");
            return false;
        }

        if (!_fateAutomation.IsTerminal)
        {
            ZodiacBuddyPlugin.PrintError("Stop the active FATE run before starting another live FATE test.");
            return false;
        }

        if (!TryGetFate(checked((ushort)fateId), out var fate))
            return false;

        StopFateGrindingInternal(true);
        StartExperimentalFiller(fate, FateExecutionPurpose.DebugFiller);
        return true;
    }

    internal void StopFateGrinding()
        => StopFateGrindingInternal(true);

    internal void StopGeneralFateGrinding()
    {
        if (_fateGrindingMode == FateGrindingMode.General)
            StopFateGrindingInternal(true);
    }

    private void StopFateGrindingInternal(bool cancelChild)
    {
        Svc.Framework.Update -= TickFateGrinding;
        var wasActive = _fateGrindingMode != FateGrindingMode.None;
        _fateGrindingMode = FateGrindingMode.None;
        _fateGrindingDesiredFateId = 0;
        _fateGrindingActiveFillerId = 0;
        _fateGrindingStatus = "Idle.";
        _fateGrindingBackoffUntil.Clear();
        _fateObservationTracker.Reset();

        if (!cancelChild || !wasActive || _fateAutomation.IsTerminal)
            return;

        if (_fateContext.Request.Purpose is FateExecutionPurpose.DebugTarget or FateExecutionPurpose.DebugFiller or FateExecutionPurpose.GeneralGrinder)
            PreemptFateAutomation(_fateContext.Request.Purpose == FateExecutionPurpose.GeneralGrinder
                ? "General FATE grinding stopped by the user."
                : "FATE test grinding stopped by the user.");
    }

    private void TickFateGrinding(IFramework _)
    {
        if (_fateGrindingMode == FateGrindingMode.None)
            return;

        if (_bookState.AutomationState == BookAutomationState.Running)
        {
            StopFateGrindingInternal(true);
            return;
        }

        if (_fateGrindingMode == FateGrindingMode.WaitForSelectedTarget)
        {
            TickSelectedFateGrinding();
            return;
        }

        TickGeneralFateGrinding();
    }

    private void TickSelectedFateGrinding()
    {
        var desired = BraveBook.GetAllFateTargets().FirstOrDefault(target => target.FateId == _fateGrindingDesiredFateId);
        if (desired.FateId == 0)
        {
            StopFateGrindingInternal(true);
            return;
        }

        if (!_fateAutomation.IsTerminal)
        {
            if (_fateContext.Request.Purpose == FateExecutionPurpose.DebugFiller
                && Service.ClientState.TerritoryType == desired.Position.TerritoryType.RowId
                && IsBookFateActionableInCurrentTerritory(desired))
            {
                var fillerId = _fateContext.Request.FateId;
                if (!RequestFatePreemption($"Required test FATE {desired.Name} ({desired.FateId}) became actionable; preempting filler {FateMetadata.GetName(fillerId)} ({fillerId})."))
                {
                    _fateGrindingStatus = _fateContext.FinishFillerBeforeYield
                        ? $"{desired.Name} is actionable; safe filler egress failed, so ZBR is finishing {FateMetadata.GetName(fillerId)} ({fillerId}) before handoff. {_fateAutomation.Status}"
                        : $"{desired.Name} is actionable; filler preemption/egress is in progress. {_fateAutomation.Status}";
                    return;
                }
                _fateGrindingLastResult = _fateAutomation.FinalResult;
                _fateGrindingActiveFillerId = 0;
                _fateGrindingStatus = $"{desired.Name} is actionable; starting the selected test FATE.";
                StartFateAutomation(CreateTargetFateRequest(desired, FateExecutionPurpose.DebugTarget, false), null, 0);
            }
            else
            {
                _fateGrindingStatus = _fateContext.Request.Purpose == FateExecutionPurpose.DebugFiller
                    ? $"Grinding filler {FateMetadata.GetName(_fateContext.Request.FateId)} ({_fateContext.Request.FateId}) while waiting for {desired.Name}. {_fateAutomation.Status}"
                    : _fateAutomation.Status;
            }
            return;
        }

        if (_fateAutomation.FinalResult.Kind != FateExecutionResultKind.None)
        {
            _fateGrindingLastResult = _fateAutomation.FinalResult;
            if (_fateContext.Request.Purpose == FateExecutionPurpose.DebugFiller
                && _fateGrindingActiveFillerId != 0)
            {
                AddFateGrindingBackoff(_fateGrindingActiveFillerId);
                _fateGrindingActiveFillerId = 0;
            }
            else if (_fateContext.Request.Purpose == FateExecutionPurpose.DebugTarget
                && _fateAutomation.FinalResult.Kind == FateExecutionResultKind.Completed)
            {
                _fateGrindingStatus = $"Selected FATE test completed: {desired.Name}.";
                Svc.Framework.Update -= TickFateGrinding;
                _fateGrindingMode = FateGrindingMode.None;
                return;
            }
        }

        if (Service.ClientState.TerritoryType != desired.Position.TerritoryType.RowId)
        {
            _fateGrindingStatus = $"Returning to {desired.ZoneName} to continue waiting for {desired.Name}.";
            StartFateAutomation(CreateTargetFateRequest(desired, FateExecutionPurpose.DebugTarget, true), null, 0);
            return;
        }

        if (IsBookFateActionableInCurrentTerritory(desired))
        {
            _fateGrindingStatus = $"{desired.Name} is actionable; starting the selected test FATE.";
            StartFateAutomation(CreateTargetFateRequest(desired, FateExecutionPurpose.DebugTarget, false), null, 0);
            return;
        }

        var excluded = new HashSet<ushort> { checked((ushort)desired.FateId) };
        var prerequisite = FateMetadata.GetPrerequisite(checked((ushort)desired.FateId));
        if (prerequisite != 0)
            excluded.Add(prerequisite);

        if (TrySelectEligibleFillerFate(FateExecutionPurpose.DebugFiller, excluded, _fateGrindingBackoffUntil, out var filler, out _))
        {
            _fateGrindingActiveFillerId = filler.FateId;
            _fateGrindingAttempts++;
            _fateGrindingStatus = $"Selected filler {FateMetadata.GetName(filler.FateId)} ({filler.FateId}) while waiting for {desired.Name}.";
            StartExperimentalFiller(filler, FateExecutionPurpose.DebugFiller);
            return;
        }

        _fateGrindingStatus = $"No eligible live filler is available in {desired.ZoneName}; watching for {desired.Name} or another local FATE.";
    }

    private void TickGeneralFateGrinding()
    {
        var now = DateTime.Now;
        _fateObservationTracker.Observe(Svc.Fates, Service.ClientState.TerritoryType, now);

        if (!_fateAutomation.IsTerminal)
        {
            _fateGrindingActiveFillerId = _fateContext.Request.FateId;
            _fateGrindingStatus = $"General grinder running {FateMetadata.GetName(_fateContext.Request.FateId)} ({_fateContext.Request.FateId}). {_fateAutomation.Status}";
            return;
        }

        if (_fateAutomation.FinalResult.Kind != FateExecutionResultKind.None
            && _fateContext.Request.Purpose == FateExecutionPurpose.GeneralGrinder
            && _fateGrindingActiveFillerId != 0)
        {
            _fateGrindingLastResult = _fateAutomation.FinalResult;
            AddFateGrindingBackoff(_fateGrindingActiveFillerId);
            _fateGrindingActiveFillerId = 0;
        }

        if (TrySelectGeneralGrinderFate(out var ranked, out var poolSummary))
        {
            var filler = ranked.Candidate.Fate;
            var travelPlan = ranked.TravelPlan;
            _fateGrindingActiveFillerId = filler.FateId;
            _fateGrindingAttempts++;
            _fateGrindingStatus = travelPlan is { } plan && plan.UsesTeleport
                ? $"General grinder selected {FateMetadata.GetName(filler.FateId)} ({filler.FateId}) as {ranked.Opportunity.Class}; teleporting via {plan.BestAetheryteName}."
                : $"General grinder selected {FateMetadata.GetName(filler.FateId)} ({filler.FateId}) as {ranked.Opportunity.Class}; travelling directly.";
            StartExperimentalFiller(filler, FateExecutionPurpose.GeneralGrinder, travelPlan);
            return;
        }

        _fateGrindingStatus = $"No eligible live FATE is currently available in this territory. {poolSummary.Describe()}";
    }

    private void AddFateGrindingBackoff(ushort fateId)
    {
        if (fateId != 0)
            _fateGrindingBackoffUntil[fateId] = DateTime.Now.AddSeconds(ExperimentalFillerBackoffSeconds);
    }

    private unsafe bool HandleBookFateGrinding(RelicNote* relicNote, BraveBook book)
    {
        if (!_bookState.FateGrindingActive)
            return false;

        if (!_bookCoordinator.IsFateOnlyRemainder(relicNote, book))
        {
            StopBookFateGrinding("book is no longer in a FATE-only remainder state", false);
            return false;
        }

        if (_bookCoordinator.TryGetActionableIncompleteFateInCurrentTerritory(relicNote, book, IsBookFateActionableInCurrentTerritory, out var required))
        {
            if (!_fateAutomation.IsTerminal && _fateContext.Request.Purpose == FateExecutionPurpose.Filler)
            {
                if (!RequestFatePreemption($"Required book FATE {required.Name} ({required.FateId}) became actionable; preempting filler {_fateContext.Request.Name} ({_fateContext.Request.FateId})."))
                {
                    _bookState.FateGrindingStatus = _fateContext.FinishFillerBeforeYield
                        ? $"Required FATE {required.Name} is actionable; safe egress failed, so ZBR is finishing the current filler before handoff. {_fateAutomation.Status}"
                        : $"Required FATE {required.Name} is actionable; filler preemption/egress is in progress. {_fateAutomation.Status}";
                    _bookState.AutomationStatus = _bookState.FateGrindingStatus;
                    return true;
                }
            }

            StopBookFateGrinding($"required FATE {required.Name} became actionable", false);
            _bookCoordinator.RequestFateSweep(relicNote, book, "required FATE became actionable during filler grinding");
            return false;
        }

        if (_bookState.FateTerritoryDwellUntil != DateTime.MinValue && DateTime.Now >= _bookState.FateTerritoryDwellUntil)
        {
            if (!_fateAutomation.IsTerminal && _fateContext.Request.Purpose == FateExecutionPurpose.Filler)
            {
                const string dwellExpiredStatus = "FATE territory dwell elapsed; finishing the current filler before the required-zone resweep.";
                _bookState.FateGrindingStatus = dwellExpiredStatus;
                _bookState.AutomationStatus = dwellExpiredStatus;
                return true;
            }

            StopBookFateGrinding("territory dwell window elapsed", false);
            _bookCoordinator.RequestFateSweep(relicNote, book, "FATE territory dwell window elapsed");
            return false;
        }

        if (_bookState.FateGrindingFillerId != 0)
        {
            if (!_fateAutomation.IsTerminal)
            {
                _bookState.FateGrindingStatus = $"Grinding filler {FateMetadata.GetName(_bookState.FateGrindingFillerId)} ({_bookState.FateGrindingFillerId}). {_fateAutomation.Status}";
                _bookState.AutomationStatus = _bookState.FateGrindingStatus;
                return true;
            }

            _bookState.FateGrindingLastResult = _fateAutomation.FinalResult;
            _bookState.FateGrindingBackoffUntil[_bookState.FateGrindingFillerId] = DateTime.Now.AddSeconds(ExperimentalFillerBackoffSeconds);
            Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] Filler {_bookState.FateGrindingFillerId} ended with {_fateAutomation.FinalResult.Kind}.");
            _bookState.FateGrindingFillerId = 0;
        }

        var incompleteInCurrentTerritory = book.Fates.Any(target => target.FateSlot is >= 0 and <= 2
            && !relicNote->IsFateComplete(target.FateSlot)
            && target.Position.TerritoryType.RowId == Service.ClientState.TerritoryType);
        if (!incompleteInCurrentTerritory)
        {
            StopBookFateGrinding("current territory no longer contains an incomplete required FATE", false);
            return false;
        }

        var excluded = new HashSet<ushort>();
        foreach (var target in book.Fates)
        {
            if (target.FateSlot is < 0 or > 2 || relicNote->IsFateComplete(target.FateSlot) || target.FateId == 0)
                continue;

            var targetId = checked((ushort)target.FateId);
            excluded.Add(targetId);
            var prerequisite = FateMetadata.GetPrerequisite(targetId);
            if (prerequisite != 0)
                excluded.Add(prerequisite);
        }

        if (TrySelectEligibleFillerFate(FateExecutionPurpose.Filler, excluded, _bookState.FateGrindingBackoffUntil, out var filler, out _))
        {
            _bookState.FateGrindingFillerId = filler.FateId;
            _bookState.FateGrindingAttempts++;
            _bookState.FateGrindingStatus = $"Grinding experimental filler {FateMetadata.GetName(filler.FateId)} ({filler.FateId}) while waiting for required book FATEs.";
            _bookState.AutomationStatus = _bookState.FateGrindingStatus;
            StartExperimentalFiller(filler, FateExecutionPurpose.Filler);
            return true;
        }

        var seconds = _bookState.FateTerritoryDwellUntil == DateTime.MinValue
            ? BookFateTerritoryDwellSeconds
            : Math.Max(1, (int)Math.Ceiling((_bookState.FateTerritoryDwellUntil - DateTime.Now).TotalSeconds));
        _bookState.FateGrindingStatus = $"No experimental filler is currently available; watching local FATEs and resweeping required zones after this territory dwell in {seconds}s.";
        _bookState.AutomationStatus = _bookState.FateGrindingStatus;
        return true;
    }

    private unsafe bool TryBeginBookFateGrinding(RelicNote* relicNote, BraveBook book)
    {
        if (!_bookCoordinator.IsFateOnlyRemainder(relicNote, book))
            return false;

        var relevantTerritory = book.Fates.Any(target => target.FateSlot is >= 0 and <= 2
            && !relicNote->IsFateComplete(target.FateSlot)
            && target.Position.TerritoryType.RowId == Service.ClientState.TerritoryType);
        if (!relevantTerritory)
            return false;

        _bookState.FateGrindingActive = true;
        _bookState.FateGrindingFillerId = 0;
        _bookState.FateGrindingStatus = "FATE-only remainder reached; starting experimental local filler grinding.";
        _bookState.FateTerritoryDwellUntil = DateTime.Now.AddSeconds(BookFateTerritoryDwellSeconds);
        Service.Plugin.PrintStepProgress("Only book FATEs remain. Grinding local FATEs while waiting for a required FATE to appear.");
        return HandleBookFateGrinding(relicNote, book);
    }

    private void StopBookFateGrinding(string reason, bool cancelChild)
    {
        if (!_bookState.FateGrindingActive)
            return;

        if (cancelChild && !_fateAutomation.IsTerminal && _fateContext.Request.Purpose == FateExecutionPurpose.Filler)
            PreemptFateAutomation($"Book FATE grinding stopped: {reason}.");

        Service.PluginLog.Verbose($"[ZodiacBuddy/BOOK] FATE grinding stopped: {reason}.");
        _bookState.FateGrindingActive = false;
        _bookState.FateGrindingFillerId = 0;
        _bookState.FateGrindingStatus = string.Empty;
    }

    private FateTravelPlan? CreateGeneralGrinderTravelPlan(IFate fate)
    {
        var player = Player.Object;
        if (player == null || fate.Position == Vector3.Zero)
        {
            Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-TRAVEL] Missing position data for {fate.FateId}; using direct travel.");
            return null;
        }

        var config = Service.Configuration.FateGrinder;
        var allowTeleport = config.UseTeleportsWhenBeneficial && !Service.Configuration.DisableTeleport;
        var plan = FateTravelPlanner.Plan(
            Service.ClientState.TerritoryType,
            player.Position,
            fate.Position,
            allowTeleport);
        return plan;
    }

    private void StartExperimentalFiller(IFate fate, FateExecutionPurpose purpose, FateTravelPlan? travelPlan = null)
    {
        var request = new FateExecutionRequest(
            fate.FateId,
            FateMetadata.GetName(fate.FateId),
            $"Territory {Service.ClientState.TerritoryType}",
            Service.ClientState.TerritoryType,
            null,
            purpose,
            false,
            travelPlan);
        StartFateAutomation(request, null, 0);
    }

    private bool TrySelectEligibleFillerFate(
        FateExecutionPurpose purpose,
        IReadOnlySet<ushort> excluded,
        IReadOnlyDictionary<ushort, DateTime> backoffUntil,
        out IFate selected,
        out FateCandidatePoolSummary poolSummary)
    {
        var candidates = EvaluateFateCandidates(purpose, excluded, backoffUntil, out poolSummary, out _);
        if (FateCandidateRanker.TrySelectLegacy(candidates, out var best))
        {
            selected = best.Fate;
            return true;
        }

        selected = default!;
        return false;
    }

    private bool TrySelectGeneralGrinderFate(
        out FateRankedCandidate selected,
        out FateCandidatePoolSummary poolSummary)
    {
        var candidates = EvaluateFateCandidates(
            FateExecutionPurpose.GeneralGrinder,
            new HashSet<ushort>(),
            _fateGrindingBackoffUntil,
            out poolSummary,
            out _);
        var now = DateTime.Now;
        var inputs = new List<FateGeneralRankInput>();
        foreach (var candidate in candidates)
        {
            if (!candidate.Eligible)
                continue;

            var travelPlan = CreateGeneralGrinderTravelPlan(candidate.Fate);
            var observation = _fateObservationTracker.GetObservation(candidate.FateId, candidate.Progress, now);
            var opportunity = FateOpportunityAnalyzer.Analyze(candidate, travelPlan, observation);
            inputs.Add(new(candidate, travelPlan, observation, opportunity));
        }

        var config = Service.Configuration.FateGrinder;
        var playerHasTwist = PlayerHasTwistOfFate();
        if (!FateCandidateRanker.TrySelectGeneral(
                inputs,
                config.PreferBonusFates,
                playerHasTwist,
                out selected,
                out var runnerUp,
                out var decisiveCriterion,
                out _))
        {
            return false;
        }

        var winnerTravel = selected.TravelSeconds == float.MaxValue ? -1f : selected.TravelSeconds;
        var runnerSummary = runnerUp.Candidate.FateId == 0
            ? "none"
            : $"FateId={runnerUp.Candidate.FateId} '{FateMetadata.GetName(runnerUp.Candidate.FateId)}' via {decisiveCriterion}";
        Service.PluginLog.Verbose($"[ZodiacBuddy/FATE-RANK] Selected {FateMetadata.GetName(selected.Candidate.FateId)} ({selected.Candidate.FateId}), opportunity={selected.Opportunity.Class}, travel={winnerTravel:F1}s, runner-up={runnerSummary}.");
        return true;
    }

    private IReadOnlyList<FateCandidateEvaluation> EvaluateFateCandidates(
        FateExecutionPurpose purpose,
        IReadOnlySet<ushort> excluded,
        IReadOnlyDictionary<ushort, DateTime> backoffUntil,
        out FateCandidatePoolSummary poolSummary,
        out FateCandidateEligibilityPolicy eligibilityPolicy)
    {
        var player = Player.Object;
        var selectionConfig = Service.Configuration.FateGrinder;
        eligibilityPolicy = purpose == FateExecutionPurpose.GeneralGrinder
            ? new FateCandidateEligibilityPolicy(
                selectionConfig.EffectiveMinimumTimeRemainingSeconds,
                selectionConfig.EffectiveMaximumProgressPercent)
            : FateCandidateEligibilityPolicy.Unrestricted;
        var context = new FateCandidateEvaluationContext(
            purpose,
            player?.Position,
            excluded,
            backoffUntil,
            Service.Configuration.FateGrinderBlacklist,
            eligibilityPolicy,
            DateTime.Now);
        var candidates = FateCandidateEvaluator.Evaluate(Svc.Fates, context);
        poolSummary = FateCandidateEvaluator.Summarize(candidates);

        return candidates;
    }

    private static bool PlayerHasTwistOfFate()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return false;
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == TwistOfFateStatusId)
                return true;
        }
        return false;
    }

}
