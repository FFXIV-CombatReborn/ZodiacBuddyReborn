using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Common.Math;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using ZodiacBuddy.Stages.Atma;
using ECommons.GameHelpers;
using ZodiacBuddy.SmartCaseUtil;

namespace ZodiacBuddy
{
    internal class TargetInfoWindow : Window
    {
        private readonly TaskManager TaskManager = new();
        public string? CurrentTarget;
        public ulong CurrentTargetId;
        public bool IsPathing => VNavmesh.Path.IsRunning();
        private bool pendingPathing = false;
        private DateTime lastPathingTime = DateTime.MinValue;
        private bool CompletedObjective => TargetingHelper.KillCount >= 3;
        private bool rsrEnabled = false;
        private readonly HashSet<ulong> RegisteredKills = [];
        private bool fallbackSuppressedPermanently = false;
        public Vector3? CurrentTargetPosition { get; private set; }
        private DateTime fallbackSuppressionUntil = DateTime.MinValue;

        public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.IsOpen = Service.Configuration.TargetInfoWindowWasOpen;

            Svc.Framework.Update += OnFrameworkUpdate;
        }
        public override void OnOpen()
        {
            Service.Configuration.TargetInfoWindowWasOpen = true;
            Service.Configuration.Save();
        }
        public override void OnClose()
        {
            Service.Configuration.TargetInfoWindowWasOpen = false;
            Service.Configuration.Save();
        }
        public void Dispose()
        {
            Service.Configuration.TargetInfoWindowWasOpen = Service.Plugin.TargetWindow?.IsOpen ?? false;
            Service.Configuration.Save();
            Svc.Framework.Update -= OnFrameworkUpdate;
            Service.CommandManager.ProcessCommand("/rotation off");
        }
        public enum TargetingState
        {
            Idle,
            AwaitingAtmaPathing,
            Active
        }
        public TargetingState State = TargetingState.Idle;
        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!IPCSubscriber.IsReady("vnavmesh"))
            {
                return;
            }
            if (State != TargetingState.Active)
                return;
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.BetweenAreas]) return;

            if (CompletedObjective)
            {
                fallbackSuppressedPermanently = true; // Hard FallBack block
                pendingPathing = false;

                if (State != TargetingState.AwaitingAtmaPathing)
                {
                    State = TargetingState.AwaitingAtmaPathing;

                    if (!Service.Configuration.AutoAdvanceEnemy)
                        Service.Plugin.PrintMessage($"All kills for {CurrentTarget} complete!");

                    CurrentTargetId = 0;
                    CurrentTargetPosition = null;
                    TargetingHelper.StoredTargetId = 0;
                    TargetingHelper.ResetAutoTargetFlag();

                    // Stop any in-progress navigation immediately so we don't path to a 4th enemy
                    if (VNavmesh.Path.IsRunning())
                        VNavmesh.Path.Stop();

                    // Only disable RSR if we're not still in combat — if the player
                    // aggro'd extras alongside the kill target, RSR must stay on so
                    // they can keep fighting. The out-of-combat branch below handles the
                    // disable once combat actually ends.
                    if (rsrEnabled && !Svc.Condition[ConditionFlag.InCombat])
                    {
                        Service.CommandManager.ProcessCommand("/rotation off");
                        rsrEnabled = false;
                    }

                    if (Service.Configuration.AutoAdvanceEnemy)
                    {
                        // Wait a few seconds so server-side chat messages (e.g. "enemy killed 3/3")
                        // and loot/XP packets arrive before the teleport kicks in.
                        var notBefore = DateTime.Now.AddSeconds(1.5);

                        // Poll until safe to teleport: out of combat, navmesh idle, and able to act.
                        TaskManager.Enqueue(() =>
                        {
                            if (DateTime.Now < notBefore) return false;
                            var atma = Service.Plugin.AtmaManager;
                            if (Svc.Condition[ConditionFlag.InCombat]) return false;
                            if (VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress()) return false;
                            if (!(atma?.CanAct ?? false)) return false;
                            return true;
                        }, 120000, "WaitForTeleportReady");
                        TaskManager.Enqueue(() =>
                        {
                            Service.Plugin.AtmaManager?.AutoAdvanceToNextEnemy();
                            return true;
                        });
                    }
                }

                // Handle post-kill combat case
                if (Svc.Condition[ConditionFlag.InCombat])
                {
                    TargetingHelper.PromoteAggroingEnemy();
                    pendingPathing = false;

                    if (!rsrEnabled)
                    {
                        TaskManager.Enqueue(() =>
                        {
                            Service.CommandManager.ProcessCommand("/rotation manual");
                            rsrEnabled = true;
                            return true;
                        });
                    }
                }
                else if (rsrEnabled)
                {
                    Service.CommandManager.ProcessCommand("/rotation off");
                    rsrEnabled = false;
                    pendingPathing = false;
                }
            }
            if (pendingPathing && !VNavmesh.Path.IsRunning() && (DateTime.Now - lastPathingTime).TotalSeconds > 2)
            {
                StartPathingToCurrentTarget();
            }
            UpdateCurrentTargetInfo();
            if (!CompletedObjective)
            {
                TargetingHelper.AutoTargetStoredIdIfVisible();

                // Stop ground pathing early if already within attack range.
                if (VNavmesh.Path.IsRunning() && CurrentTargetPosition.HasValue && Player.Object != null)
                {
                    var distToTarget = Vector3.Distance(Player.Object.Position, CurrentTargetPosition.Value);
                    if (distToTarget <= 20f)
                    {
                        VNavmesh.Path.Stop();
                        pendingPathing = false;
                    }
                }
            }
        }
        
        public void SetTarget(string name, ulong id = 0)
        {
            RegisteredKills.Clear();
            fallbackSuppressedPermanently = false;
            if (State == TargetingState.Active)
                return;

            CurrentTarget = SmartCaseHelper.SmartTitleCase(name);
            CurrentTargetId = id;
            CurrentTargetPosition = null;

            if (!string.IsNullOrWhiteSpace(name))
                TargetingHelper.StartKillTracking(name);

            if (id != 0)
            {
                TargetingHelper.StoredTargetId = id;
                TargetingHelper.ResetAutoTargetFlag();
            }

            State = TargetingState.AwaitingAtmaPathing;
        }

        // This is called by AtmaManager once /vnav moveflag finishes
        public void OnAtmaPathingComplete()
        {
            fallbackSuppressedPermanently = false;
            State = TargetingState.Active;
            pendingPathing = true;
            fallbackSuppressionUntil = DateTime.Now.AddSeconds(0.5);
            TaskManager.Enqueue(new Func<bool?>(() =>
            {
                Service.CommandManager.ProcessCommand("/rotation manual");
                rsrEnabled = true;
                return true;
            }));
        }

        private void StartPathingToCurrentTarget()
        {
            if (CompletedObjective)
            {
                pendingPathing = false;
                return;
            }
            if (VNavmesh.Path.IsRunning())
            {
                pendingPathing = true;
                return;
            }
            // Never start a new path mid-cast — vnavmesh movement cancels the cast bar.
            if (Svc.Condition[ConditionFlag.Casting])
            {
                pendingPathing = false;
                return;
            }
            // Combat hysteresis: while engaged, don't re-path just because the enemy drifted
            // slightly. Only issue a new path if they're genuinely far away (> 28y).
            // This prevents the oscillation loop where every 2y of enemy movement issues a
            // new path that cancels the current attack.
            if (Svc.Condition[ConditionFlag.InCombat] && Player.Object != null && CurrentTargetPosition != null)
            {
                var combatDist = Vector3.Distance(Player.Object.Position, CurrentTargetPosition.Value);
                if (combatDist <= 28f)
                {
                    pendingPathing = false;
                    return;
                }
            }
            if (CurrentTargetPosition != null)
            {
                var pos = CurrentTargetPosition.Value;

                // Don't start a new path if already within attack range.
                if (Player.Object != null)
                {
                    var dist = Vector3.Distance(Player.Object.Position, pos);
                    if (dist <= 20f)
                    {
                        pendingPathing = false;
                        return;
                    }
                }

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
                VNavmesh.SimpleMove.PathfindAndMoveTo(pos, false);

                Service.Plugin.PrintMessage($"Pathing to {CurrentTarget} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
                lastPathingTime = DateTime.Now;
                pendingPathing = false;
            }
            else
            {
                if (fallbackSuppressedPermanently || TargetingHelper.KillCount >= 3 || Svc.Condition[ConditionFlag.InCombat])
                {
                    pendingPathing = false;
                    return;
                }
                Service.CommandManager.ProcessCommand("/vnav moveflag");
                Service.Plugin.PrintMessage($"No {CurrentTarget} found nearby — pathing to map flag. Will engage when it appears.");
                AtmaManager.OnFallbackPathIssued?.Invoke();
                lastPathingTime = DateTime.Now;
                pendingPathing = false;
            }
        }

        public void UpdateCurrentTargetInfo()
        {
            if (!string.IsNullOrEmpty(CurrentTarget))
            {
                var previousId = CurrentTargetId;

                var playerPosition = Player.Object?.Position ?? Vector3.Zero;
                ICharacter? match = null;
                var bestDistance = float.MaxValue;

                foreach (var obj in Svc.Objects)
                {
                    if (obj.ObjectKind != ObjectKind.BattleNpc)
                        continue;
                    if (obj is not ICharacter c)
                        continue;
                    if (c.CurrentHp <= 0)
                        continue;
                    if (!obj.Name.TextValue.Equals(CurrentTarget, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var distance = Vector3.Distance(c.Position, playerPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        match = c;
                    }
                }

                if (match != null)
                {
                    // Only switch if current ID is 0 or the current target is no longer valid
                    if (CurrentTargetId == 0)
                    {
                        CurrentTargetId = match.GameObjectId;
                        TargetingHelper.StoredTargetId = match.GameObjectId;
                        TargetingHelper.ResetAutoTargetFlag();
                    }
                    else if (match.GameObjectId != CurrentTargetId)
                    {
                        ICharacter? previousTarget = null;
                        foreach (var obj in Svc.Objects)
                        {
                            if (obj is ICharacter character &&
                                character.ObjectKind == ObjectKind.BattleNpc &&
                                character.GameObjectId == CurrentTargetId)
                            {
                                previousTarget = character;
                                break;
                            }
                        }

                        if (previousTarget == null || previousTarget.CurrentHp == 0)
                        {
                            if (CurrentTargetId != 0 && !RegisteredKills.Contains(CurrentTargetId))
                            {
                                RegisteredKills.Add(CurrentTargetId);
                                TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");
                            }
                            else
                            {
                                // duplicate or zero-id, skip
                            }
                            CurrentTargetId = 0;
                            CurrentTargetPosition = null;
                            TargetingHelper.StoredTargetId = 0;
                            TargetingHelper.ResetAutoTargetFlag();
                        }
                        else
                        {
                            // previous target still alive — keep tracking it
                        }
                        CurrentTargetId = match.GameObjectId;
                        TargetingHelper.StoredTargetId = match.GameObjectId;
                        TargetingHelper.ResetAutoTargetFlag();
                    }
                    bool shouldForcePathing = CurrentTargetPosition == null;
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
                        if (CurrentTargetId != 0 && !RegisteredKills.Contains(CurrentTargetId))
                        {
                            RegisteredKills.Add(CurrentTargetId);
                            TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");
                        }
                        else
                        {
                            // duplicate or zero-id, skip
                        }

                        CurrentTargetId = 0;
                        CurrentTargetPosition = null;
                        TargetingHelper.StoredTargetId = 0;
                        TargetingHelper.ResetAutoTargetFlag();

                        if (!CompletedObjective)
                            pendingPathing = true;
                    }
                }
                return;
            }

            var target = Svc.Targets.Target;
            if (target != null && target.ObjectKind == ObjectKind.BattleNpc)
            {
                CurrentTarget = SmartCaseHelper.SmartTitleCase(target.Name.TextValue.Trim());
                CurrentTargetId = target.GameObjectId;
                CurrentTargetPosition = target.Position;
            }
        }


        public override void Draw()
        {
            bool atmaEnabled = Service.Configuration.IsAtmaManagerEnabled;
            if (ImGui.Checkbox("Enable Atma Manager", ref atmaEnabled))
            {
                Service.Configuration.IsAtmaManagerEnabled = atmaEnabled;
                Service.Configuration.Save();
                if (!atmaEnabled)
                    Service.CommandManager.ProcessCommand("/rotation off");
            }

            ImGui.Separator();

            if (CompletedObjective)
            {
                UpdateStatusUIOnly();
                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentTarget))
            {
                ImGui.Text("No target selected.");
            }
            else
            {
                ImGui.Text($"Current Target: {CurrentTarget}");
                ImGui.Text($"GameObjectId: {CurrentTargetId}");
                if (CurrentTargetPosition.HasValue)
                {
                    var pos = CurrentTargetPosition.Value;
                    ImGui.Text($"Position: X: {pos.X:F1}, Y: {pos.Y:F1}, Z: {pos.Z:F1}");
                }
            }

            ImGui.Separator();

            Vector4 color;
            string status;

            if (!VNavmesh.Nav.IsReady())
            {
                status = "Navmesh Not Ready";
                color = new Vector4(1f, 0f, 0f, 1f);
            }
            else if (VNavmesh.Nav.PathfindInProgress())
            {
                status = "Generating Path...";
                color = new Vector4(1f, 1f, 0f, 1f);
            }
            else if (VNavmesh.Path.IsRunning())
            {
                status = "Pathing";
                color = new Vector4(0f, 1f, 0f, 1f);
            }
            else
            {
                status = "Idle";
                color = new Vector4(1f, 1f, 1f, 1f);
            }

            ImGui.TextColored(color, $"Status: {status}");

            string killStatus;
            Vector4 killColor;

            if (TargetingHelper.KillCount >= 3)
            {
                killStatus = "Kill Target Complete!";
                killColor = new Vector4(0f, 1f, 0f, 1f);
            }
            else
            {
                killStatus = $"Kills: {TargetingHelper.KillCount} / 3";
                killColor = new Vector4(1f, 1f, 1f, 1f);
            }

            ImGui.TextColored(killColor, killStatus);
        }

        private void UpdateStatusUIOnly()
        {
            ImGui.Text($"Current Target: {CurrentTarget}");
            ImGui.Text($"Kills: {TargetingHelper.KillCount} / 3");

            Vector4 statusColor;
            string statusText;

            if (!VNavmesh.Nav.IsReady())
            {
                statusText = "Navmesh Not Ready";
                statusColor = new Vector4(1f, 0f, 0f, 1f);
            }
            else if (VNavmesh.Nav.PathfindInProgress())
            {
                statusText = "Generating Path...";
                statusColor = new Vector4(1f, 1f, 0f, 1f);
            }
            else if (VNavmesh.Path.IsRunning())
            {
                statusText = "Pathing";
                statusColor = new Vector4(0f, 1f, 0f, 1f);
            }
            else
            {
                statusText = "Idle";
                statusColor = new Vector4(1f, 1f, 1f, 1f);
            }

            ImGui.Separator();
            ImGui.TextColored(statusColor, $"Status: {statusText}");
            ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Kill Target Complete!");
        }
    }
}
