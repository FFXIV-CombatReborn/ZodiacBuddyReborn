using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using System;
using System.Collections.Generic;
using ZodiacBuddy.SmartCaseUtil;
using ZodiacBuddy.Systems.Combat;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class EnemyAutomationController : IDisposable
{
    private const byte MonsterObjectiveRequiredProgress = 3;
    private const float EnemyCombatActivationDistance = 40f;
    private const float EnemyAggroCleanupPathMinDistance = 5f;
    private const float EnemyAggroCleanupPathMaxDistance = 50f;
    private static readonly TimeSpan MonsterCreditGrace = TimeSpan.FromSeconds(5);

    private readonly AnimusManager owner;
    private readonly HashSet<ulong> registeredKills = [];
    private bool pendingPathing;
    private DateTime lastPathingTime = DateTime.MinValue;
    private readonly RotationSolverLease rotationSolver = new();
    private bool wasInCombat;
    private DateTime rsrRestartNotBefore = DateTime.MinValue;
    private bool fallbackSuppressedPermanently;
    private bool incidentalAggroClearanceActive;
    private int monsterSlot = -1;
    private uint monsterRelicNoteId;
    private int lastMonsterProgress = -1;
    private int initialMonsterProgress = -1;
    private int observedMonsterDeaths;
    private DateTime finalMonsterCreditGraceUntil = DateTime.MinValue;

    internal EnemyAutomationController(AnimusManager owner)
    {
        this.owner = owner;
        wasInCombat = Svc.Condition[ConditionFlag.InCombat];
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    internal string? CurrentTargetName { get; private set; }
    internal ulong CurrentTargetId { get; private set; }
    internal Vector3? CurrentTargetPosition { get; private set; }
    internal EnemyAutomationState State { get; private set; } = EnemyAutomationState.Idle;

    private bool CompletedObjective => TryGetMonsterBookProgress(out var progress) && progress >= MonsterObjectiveRequiredProgress;

    private int ExpectedMonsterProgress
        => initialMonsterProgress < 0 ? 0 : Math.Min(MonsterObjectiveRequiredProgress, initialMonsterProgress + observedMonsterDeaths);

    private bool WaitingForFinalBookCredit
    {
        get
        {
            if (DateTime.Now >= finalMonsterCreditGraceUntil || ExpectedMonsterProgress < MonsterObjectiveRequiredProgress)
                return false;

            return TryGetMonsterBookProgress(out var progress)
                && progress < MonsterObjectiveRequiredProgress
                && GetPendingMonsterCredits(progress) > 0;
        }
    }

    public void Dispose()
    {
        RequestRotationSolverStop("enemy controller disposed");
        ProcessRotationSolverStopRetry();
        Svc.Framework.Update -= OnFrameworkUpdate;
    }

    internal EnemyAutomationSnapshot GetSnapshot()
    {
        byte? progress = TryGetMonsterBookProgress(out var currentProgress) ? currentProgress : null;
        var pendingCredits = progress is byte value ? GetPendingMonsterCredits(value) : 0;
        var navigation = !VNavmesh.Nav.IsReady()
            ? EnemyNavigationDisplayState.NavmeshNotReady
            : VNavmesh.Nav.PathfindInProgress()
                ? EnemyNavigationDisplayState.GeneratingPath
                : VNavmesh.Path.IsRunning()
                    ? EnemyNavigationDisplayState.Pathing
                    : EnemyNavigationDisplayState.None;

        return new EnemyAutomationSnapshot(
            State,
            CurrentTargetName,
            CurrentTargetId,
            progress,
            pendingCredits,
            WaitingForFinalBookCredit,
            incidentalAggroClearanceActive || (CompletedObjective && Svc.Condition[ConditionFlag.InCombat]),
            navigation);
    }

    internal void CancelAutomationNavigation()
    {
        RequestRotationSolverStop("automation navigation cancelled");
        pendingPathing = false;
        incidentalAggroClearanceActive = false;
        CurrentTargetName = null;
        CurrentTargetId = 0;
        CurrentTargetPosition = null;
        TargetingHelper.StoredTargetId = 0;
        TargetingHelper.ResetAutoTargetFlag();
        monsterSlot = -1;
        monsterRelicNoteId = 0;
        lastMonsterProgress = -1;
        initialMonsterProgress = -1;
        observedMonsterDeaths = 0;
        finalMonsterCreditGraceUntil = DateTime.MinValue;
        State = EnemyAutomationState.Idle;
    }

        internal void SetTarget(string name, int bookMonsterSlot, uint relicNoteId, ulong id = 0)
        {
            registeredKills.Clear();
            fallbackSuppressedPermanently = false;
            incidentalAggroClearanceActive = false;
            if (State == EnemyAutomationState.Active)
                return;

            CurrentTargetName = SmartCaseHelper.SmartTitleCase(name);
            CurrentTargetId = id;
            CurrentTargetPosition = null;

            monsterSlot = bookMonsterSlot;
            monsterRelicNoteId = relicNoteId;
            lastMonsterProgress = -1;
            initialMonsterProgress = -1;
            observedMonsterDeaths = 0;
            finalMonsterCreditGraceUntil = DateTime.MinValue;
            UpdateMonsterBookProgress();

            if (id != 0)
            {
                TargetingHelper.StoredTargetId = id;
                TargetingHelper.ResetAutoTargetFlag();
            }

            State = EnemyAutomationState.AwaitingAtmaPathing;
        }

        internal void OnAtmaPathingComplete()
        {
            fallbackSuppressedPermanently = false;
            incidentalAggroClearanceActive = false;
            Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Enemy navigation complete; enabling target acquisition.");
            State = EnemyAutomationState.Active;
            pendingPathing = true;
        }

        internal bool BeginIncidentalAggroClearanceForEnemyTravel()
        {
            if (!owner.HasActiveAutomationRun || CompletedObjective || WaitingForFinalBookCredit || !Svc.Condition[ConditionFlag.InCombat])
                return false;

            if (!incidentalAggroClearanceActive)
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Enemy travel is blocked by combat; temporarily clearing incidental aggro before resuming mount/flight.");

            incidentalAggroClearanceActive = true;
            pendingPathing = true;
            var promoted = PromoteAggroingEnemyAndMaintainFateSync(true);
            StartRotationSolver();
            return promoted;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            UpdateMonsterBookProgress();
            ExpirePendingMonsterCreditsIfNeeded();
            UpdateRotationSolverLifecycle();

            if (!IPCSubscriber.IsReady("vnavmesh"))
            {
                return;
            }
            if (State != EnemyAutomationState.Active)
                return;
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.BetweenAreas]) return;
            if (!owner.HasActiveAutomationRun)
            {
                pendingPathing = false;
                incidentalAggroClearanceActive = false;
                return;
            }

            if (incidentalAggroClearanceActive)
            {
                if (Svc.Condition[ConditionFlag.InCombat])
                {
                    PromoteAggroingEnemyAndMaintainFateSync(true);
                    StartRotationSolver();
                    return;
                }

                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Incidental aggro cleared; resuming the pending enemy travel request.");
                owner.StopEnemyAggroCleanupNavigation();
                incidentalAggroClearanceActive = false;
                pendingPathing = true;
            }

            if (CompletedObjective)
            {
                fallbackSuppressedPermanently = true;
                pendingPathing = false;

                if (State != EnemyAutomationState.AwaitingAtmaPathing)
                {
                    Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Book confirmed monster objective complete. Locking logic and clearing target.");
                    Service.Plugin.PrintStepProgress($"Book credit confirmed for {CurrentTargetName} (3/3).");
                    State = EnemyAutomationState.AwaitingAtmaPathing;
                    owner.PrepareCompletedEnemyObjectiveForPlannerRelease();

                    CurrentTargetId = 0;
                    CurrentTargetPosition = null;
                    TargetingHelper.StoredTargetId = 0;
                    TargetingHelper.ResetAutoTargetFlag();

                }

                if (Svc.Condition[ConditionFlag.InCombat])
                {
                    PromoteAggroingEnemyAndMaintainFateSync(false);
                    pendingPathing = false;
                    StartRotationSolver();
                }
                else
                {
                    RequestRotationSolverStop("enemy objective completed");
                    pendingPathing = false;
                }

                return;
            }

            if (WaitingForFinalBookCredit)
            {
                pendingPathing = false;
                CurrentTargetId = 0;
                CurrentTargetPosition = null;
                TargetingHelper.StoredTargetId = 0;
                TargetingHelper.ResetAutoTargetFlag();
                RequestRotationSolverStop("awaiting enemy book credit");
                return;
            }

            UpdateCurrentTargetInfo();
            if (pendingPathing && !VNavmesh.Path.IsRunning() && (DateTime.Now - lastPathingTime).TotalSeconds > 2)
            {
                StartPathingToCurrentTarget();
            }
            if (!CompletedObjective)
            {
                TargetingHelper.AutoTargetStoredIdIfVisible();
            }
        }

        private void StartPathingToCurrentTarget()
        {
            if (VNavmesh.Path.IsRunning()
                || VNavmesh.SimpleMove.PathfindInProgress()
                || VNavmesh.Nav.PathfindInProgress()
                || owner.IsEnemySpawnRelocationInProgress
                || owner.IsEnemyTargetFollowRecoveryInProgress)
            {
                pendingPathing = true;
                return;
            }
            if (CurrentTargetPosition != null)
            {
                var pos = CurrentTargetPosition.Value;
                if (!IPCSubscriber.IsReady("vnavmesh"))
                {
                    pendingPathing = true;
                    return;
                }
                if (!VNavmesh.Nav.IsReady())
                {
                    pendingPathing = true;
                    return;
                }
                if (Svc.Condition[ConditionFlag.BetweenAreas])
                {
                    pendingPathing = true;
                    return;
                }
                if (!owner.TryStartTargetNavigation(pos, result => HandleTargetNavigationResult(pos, result)))
                {
                    Service.PluginLog.Warning($"[ZodiacBuddy/ENEMY] Pathing request to {CurrentTargetName} was rejected.");
                    lastPathingTime = DateTime.Now;
                    pendingPathing = true;
                    return;
                }

                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Pathing to {CurrentTargetName} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
                lastPathingTime = DateTime.Now;
                pendingPathing = false;
            }
            else
            {
                if (fallbackSuppressedPermanently || CompletedObjective || WaitingForFinalBookCredit)
                {
                    pendingPathing = false;
                    return;
                }
                if (Svc.Condition[ConditionFlag.InCombat])
                {
                    BeginIncidentalAggroClearanceForEnemyTravel();
                    pendingPathing = true;
                    return;
                }
                if (!VNavmesh.Nav.IsReady())
                {
                    pendingPathing = true;
                    return;
                }
                if (!owner.TryStartNextEnemySpawnNavigation(HandleKnownSpawnNavigationResult))
                {
                    Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] No alternate reachable known spawn is available. Remaining in place and watching for the target.");
                    lastPathingTime = DateTime.Now;
                    pendingPathing = false;
                    return;
                }

                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] No enemy found nearby; moving to another known spawn area.");
                lastPathingTime = DateTime.Now;
                pendingPathing = false;
            }
        }

        private void HandleKnownSpawnNavigationResult(AnimusNavigationResult result)
        {
            if (result == AnimusNavigationResult.Cancelled
                || State != EnemyAutomationState.Active
                || CompletedObjective
                || WaitingForFinalBookCredit
                || !owner.HasActiveAutomationRun)
                return;

            lastPathingTime = DateTime.Now;
            pendingPathing = true;
        }

        private void HandleTargetNavigationResult(Vector3 destination, AnimusNavigationResult result)
        {
            if (result is AnimusNavigationResult.Arrived or AnimusNavigationResult.Cancelled
                || State != EnemyAutomationState.Active
                || CompletedObjective
                || WaitingForFinalBookCredit
                || CurrentTargetPosition != destination
                || !owner.HasActiveAutomationRun)
                return;

            lastPathingTime = DateTime.Now;
            pendingPathing = true;
        }

        private void UpdateCurrentTargetInfo()
        {
            if (owner.IsEnemySpawnRelocationInProgress)
                return;

            if (!string.IsNullOrEmpty(CurrentTargetName))
            {
                var previousId = CurrentTargetId;
                var playerPosition = Player.Object?.Position ?? Vector3.Zero;
                ICharacter? match = null;
                var bestDistance = float.MaxValue;

                if (CurrentTargetId != 0)
                {
                    foreach (var obj in Svc.Objects)
                    {
                        if (obj is not ICharacter current
                            || current.ObjectKind != ObjectKind.BattleNpc
                            || current.GameObjectId != CurrentTargetId
                            || current.CurrentHp <= 0
                            || !current.Name.TextValue.Equals(CurrentTargetName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        match = current;
                        bestDistance = Vector3.Distance(current.Position, playerPosition);
                        break;
                    }
                }

                if (match == null)
                {
                    foreach (var obj in Svc.Objects)
                    {
                        if (obj.ObjectKind != ObjectKind.BattleNpc)
                            continue;
                        if (obj is not ICharacter c)
                            continue;
                        if (c.CurrentHp <= 0)
                            continue;
                        if (!obj.Name.TextValue.Equals(CurrentTargetName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var distance = Vector3.Distance(c.Position, playerPosition);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            match = c;
                        }
                    }
                }

                if (match != null)
                {
                    if (CurrentTargetId != 0 && match.GameObjectId != CurrentTargetId)
                    {
                        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Previous target {CurrentTargetId} gone or dead. Checking for duplicate registration.");

                        RegisterPotentialMonsterCredit(CurrentTargetId);
                        CurrentTargetId = 0;
                        CurrentTargetPosition = null;
                        TargetingHelper.StoredTargetId = 0;
                        TargetingHelper.ResetAutoTargetFlag();

                        if (WaitingForFinalBookCredit)
                        {
                            pendingPathing = false;
                            RequestRotationSolverStop("awaiting enemy book credit");
                            return;
                        }
                    }

                    if (CurrentTargetId == 0)
                    {
                        if (previousId != 0)
                            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Switching target from {previousId} to {match.GameObjectId}.");

                        CurrentTargetId = match.GameObjectId;
                        TargetingHelper.StoredTargetId = match.GameObjectId;
                        TargetingHelper.ResetAutoTargetFlag();
                        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Set new target ID: {CurrentTargetId}");
                        var owningFateId = FateTargeting.GetFateId(match);
                        if (owningFateId != 0)
                            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Selected book enemy '{match.Name.TextValue}' id={match.GameObjectId} is currently owned by FateId={owningFateId}.");
                    }

                    TryLevelSyncForEnemyTarget(match);
                    if (bestDistance <= EnemyCombatActivationDistance || Svc.Condition[ConditionFlag.InCombat])
                        StartRotationSolver();

                    var shouldForcePathing = CurrentTargetPosition == null;
                    if (shouldForcePathing || Vector3.Distance(CurrentTargetPosition!.Value, match.Position) > 2f)
                    {
                        CurrentTargetPosition = match.Position;
                        StartPathingToCurrentTarget();
                    }
                    TargetingHelper.AutoTargetStoredIdIfVisible();
                }
                else
                {
                    if (CurrentTargetId != 0 || CurrentTargetPosition != null)
                    {
                        Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Lost sight of {CurrentTargetName}, checking for kill...");

                        RegisterPotentialMonsterCredit(CurrentTargetId);

                        CurrentTargetId = 0;
                        CurrentTargetPosition = null;
                        TargetingHelper.StoredTargetId = 0;
                        TargetingHelper.ResetAutoTargetFlag();

                        if (WaitingForFinalBookCredit)
                        {
                            pendingPathing = false;
                            RequestRotationSolverStop("awaiting enemy book credit");
                        }
                        else if (!CompletedObjective)
                        {
                            pendingPathing = true;
                        }
                    }
                }
                return;
            }

            var target = Svc.Targets.Target;
            if (target != null && target.ObjectKind == ObjectKind.BattleNpc)
            {
                CurrentTargetName = SmartCaseHelper.SmartTitleCase(target.Name.TextValue.Trim());
                CurrentTargetId = target.GameObjectId;
                CurrentTargetPosition = target.Position;
            }
        }

        private unsafe void UpdateMonsterBookProgress()
        {
            if (monsterSlot is < 0 or > 9 || monsterRelicNoteId == 0)
                return;

            var relicNote = RelicNote.Instance();
            if (relicNote == null || relicNote->RelicNoteId != monsterRelicNoteId)
                return;

            var progress = relicNote->GetMonsterProgress(monsterSlot);
            if (initialMonsterProgress < 0)
                initialMonsterProgress = progress;
            if (progress == lastMonsterProgress)
                return;

            lastMonsterProgress = progress;
            if (progress >= MonsterObjectiveRequiredProgress)
                finalMonsterCreditGraceUntil = DateTime.MinValue;

            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Book progress relicNoteId={monsterRelicNoteId} slot={monsterSlot} target='{CurrentTargetName}' progress={progress} observedDeaths={observedMonsterDeaths} pendingCredits={GetPendingMonsterCredits(progress)}.");
        }

    private int GetPendingMonsterCredits(byte progress)
        => Math.Max(0, ExpectedMonsterProgress - progress);

        private void RegisterPotentialMonsterCredit(ulong gameObjectId)
        {
            if (gameObjectId == 0)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Skipping duplicate or zero-ID enemy credit observation for {gameObjectId}.");
                return;
            }

            if (registeredKills.Contains(gameObjectId))
            {
                if (ExpectedMonsterProgress >= MonsterObjectiveRequiredProgress
                    && TryGetMonsterBookProgress(out var duplicateProgress)
                    && duplicateProgress < MonsterObjectiveRequiredProgress)
                {
                    finalMonsterCreditGraceUntil = DateTime.Now.Add(MonsterCreditGrace);
                    Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Duplicate final target disappearance id={gameObjectId} refreshed the book-credit grace at progress={duplicateProgress} without double-counting the observed kill.");
                }

                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Skipping duplicate or zero-ID enemy credit observation for {gameObjectId}.");
                return;
            }

            registeredKills.Add(gameObjectId);
            if (!TryGetMonsterBookProgress(out var progress) || progress >= MonsterObjectiveRequiredProgress)
                return;

            if (initialMonsterProgress < 0)
                initialMonsterProgress = progress;

            observedMonsterDeaths++;
            finalMonsterCreditGraceUntil = DateTime.Now.Add(MonsterCreditGrace);
            var pendingCredits = GetPendingMonsterCredits(progress);
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Observed target death id={gameObjectId} target='{CurrentTargetName}' bookProgress={progress} observedDeaths={observedMonsterDeaths} pendingCredits={pendingCredits} expectedProgress={ExpectedMonsterProgress}.");

            if (ExpectedMonsterProgress >= MonsterObjectiveRequiredProgress && pendingCredits > 0)
                Service.PluginLog.Verbose("[ZodiacBuddy/ENEMY] Waiting for the game to confirm the final monster-book credit before selecting another target.");
        }

        private void ExpirePendingMonsterCreditsIfNeeded()
        {
            if (finalMonsterCreditGraceUntil == DateTime.MinValue || DateTime.Now < finalMonsterCreditGraceUntil || CompletedObjective)
                return;

            if (ExpectedMonsterProgress >= MonsterObjectiveRequiredProgress && TryGetMonsterBookProgress(out var progress) && GetPendingMonsterCredits(progress) > 0)
            {
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Final book-credit grace expired at progress={progress}; resuming target acquisition and spawn search.");
                pendingPathing = true;
                lastPathingTime = DateTime.MinValue;
            }

            finalMonsterCreditGraceUntil = DateTime.MinValue;
        }

    private unsafe bool TryGetMonsterBookProgress(out byte progress)
    {
        progress = 0;
        if (monsterSlot is < 0 or > 9 || monsterRelicNoteId == 0)
            return false;

        var relicNote = RelicNote.Instance();
        if (relicNote == null || relicNote->RelicNoteId != monsterRelicNoteId)
            return false;

        progress = relicNote->GetMonsterProgress(monsterSlot);
        return true;
    }

        private void UpdateRotationSolverLifecycle()
        {
            var inCombat = Svc.Condition[ConditionFlag.InCombat];
            if (wasInCombat && !inCombat)
            {
                rsrRestartNotBefore = DateTime.Now.AddMilliseconds(750);
                RequestRotationSolverStop("combat ended");
            }

            if (inCombat
                && owner.HasActiveAutomationRun
                && !string.IsNullOrWhiteSpace(CurrentTargetName)
                && !WaitingForFinalBookCredit)
            {
                StartRotationSolver();
            }

            if (CompletedObjective
                && State == EnemyAutomationState.AwaitingAtmaPathing
                && inCombat
                && owner.HasActiveAutomationRun)
            {
                PromoteAggroingEnemyAndMaintainFateSync(false);
                StartRotationSolver();
            }

            wasInCombat = inCombat;
            ProcessRotationSolverStopRetry();
        }

        private bool PromoteAggroingEnemyAndMaintainFateSync(bool incidentalClearance)
        {
            var target = FateTargeting.GetBestAggroTarget(Svc.Targets.Target?.GameObjectId ?? 0);
            if (target == null)
                return false;

            if (Svc.Targets.Target?.GameObjectId != target.GameObjectId)
            {
                TargetingHelper.TryTargetById(target.GameObjectId);
                var phase = incidentalClearance ? "incidental" : "post-objective";
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Aggroing enemy '{target.Name.TextValue}' promoted for {phase} cleanup; targetObjectId={target.TargetObjectId}. Player, companion, pet, and player-owned actors are protected cleanup targets.");
            }

            if (target is ICharacter character)
                TryLevelSyncForEnemyTarget(character);

            var player = Player.Object;
            if (player == null)
                return true;

            var distance = Vector3.Distance(player.Position, target.Position);
            if (distance <= EnemyAggroCleanupPathMinDistance
                || distance > EnemyAggroCleanupPathMaxDistance
                || VNavmesh.Path.IsRunning()
                || !IPCSubscriber.IsReady("vnavmesh")
                || !VNavmesh.Nav.IsReady())
                return true;

            if (owner.TryStartEnemyAggroCleanupNavigation(target.GameObjectId, target.Position))
            {
                var phase = incidentalClearance ? "incidental" : "post-objective";
                Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Pathing to promoted {phase} attacker '{target.Name.TextValue}' at {distance:F1}y while clearing combat.");
            }

            return true;
        }

        private unsafe void TryLevelSyncForEnemyTarget(ICharacter target)
        {
            var fateId = FateTargeting.GetFateId(target);
            if (fateId == 0)
                return;

            var manager = FateManager.Instance();
            if (manager == null || manager->SyncedFateId == fateId || manager->GetCurrentFateId() != fateId)
                return;

            if (!EzThrottler.Throttle($"ZBR_EnemyFateSync_{fateId}", 1500))
                return;

            manager->LevelSync();
            Service.PluginLog.Verbose($"[ZodiacBuddy/ENEMY] Target '{target.Name.TextValue}' id={target.GameObjectId} is owned by FateId={fateId}; requested level sync before combat.");
        }

        internal void StartTeleportAggroCleanupRotationSolver()
            => StartRotationSolver();

        internal void StopTeleportAggroCleanupRotationSolver()
            => RequestRotationSolverStop("teleport-interrupt aggro cleared");

        private void StartRotationSolver()
        {
            if (DateTime.Now < rsrRestartNotBefore)
                return;

            rotationSolver.Acquire(
                "[ZodiacBuddy/ENEMY] Enabled RotationSolverReborn manual mode through IPC.",
                "[ZodiacBuddy/ENEMY] RSR manual-mode IPC was unavailable; command fallback used.");
        }

        private void RequestRotationSolverStop(string reason)
        {
            rotationSolver.RequestStop(
                $"[ZodiacBuddy/ENEMY] RotationSolverReborn stop requested because {reason}.",
                "[ZodiacBuddy/ENEMY] Disabled RotationSolverReborn through IPC.",
                "[ZodiacBuddy/ENEMY] RotationSolverReborn stop IPC remained unavailable after the retry window; command fallbacks were issued.");
        }

        private void ProcessRotationSolverStopRetry()
        {
            rotationSolver.ProcessStopRetry(
                "[ZodiacBuddy/ENEMY] Disabled RotationSolverReborn through IPC.",
                "[ZodiacBuddy/ENEMY] RotationSolverReborn stop IPC remained unavailable after the retry window; command fallbacks were issued.");
        }

}
