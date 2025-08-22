using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Common.Math;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ZodiacBuddy;
using ZodiacBuddy.Helpers;
using ZodiacBuddy.SmartCaseUtil;
using ZodiacBuddy.Stages.Atma;

namespace ZodiacBuddy.TargetWindow
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
        private readonly HashSet<ulong> RegisteredKills = new();
        private bool fallbackSuppressedPermanently = false;
        public Vector3? CurrentTargetPosition { get; private set; }
        private DateTime fallbackSuppressionUntil = DateTime.MinValue;
        
        public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.IsOpen = true;
            Svc.Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
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
                    Service.PluginLog.Debug("Reached 3 kills. Locking logic and clearing target.");
                    State = TargetingState.AwaitingAtmaPathing;

                    CurrentTargetId = 0;
                    CurrentTargetPosition = null;
                    TargetingHelper.StoredTargetId = 0;
                    TargetingHelper.ResetAutoTargetFlag();

                    if (rsrEnabled)
                    {
                        Service.PluginLog.Debug("Kill complete — disabling RSR via /rotation off.");
                        Service.CommandManager.ProcessCommand("/rotation off");
                        rsrEnabled = false;
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
            Service.PluginLog.Debug("Atma Pathing complete, unlocking targeting logic.");
            State = TargetingState.Active;
            pendingPathing = true;
            fallbackSuppressionUntil = DateTime.Now.AddSeconds(0.5);
            if (!rsrEnabled)
            {
                Service.PluginLog.Debug("Enabling RSR via /rotation manual.");
                TaskManager.Enqueue(new Func<bool?>(() =>
                {
                    Service.CommandManager.ProcessCommand("/rotation manual");
                    rsrEnabled = true;
                    return true;
                }));
            }
        }

        private void StartPathingToCurrentTarget()
        {
            if (VNavmesh.Path.IsRunning())
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
                VNavmesh.SimpleMove.PathfindAndMoveTo(pos, false);

                Service.ChatGui.Print($"Pathing to {CurrentTarget} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
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
                string fallbackCommand = "/vnav moveflag";
                Service.PluginLog.Debug($"Issuing fallback pathing: {fallbackCommand}");
                Service.CommandManager.ProcessCommand(fallbackCommand);
                Service.ChatGui.Print("No enemy found nearby. Pathing to map flag.");
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

                var match = Svc.Objects
                    .Where(obj =>
                        obj.ObjectKind == ObjectKind.BattleNpc &&
                        obj is ICharacter c &&
                        c.CurrentHp > 0 &&
                        obj.Name.TextValue.Equals(CurrentTarget, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(obj => Vector3.Distance(obj.Position, Svc.ClientState.LocalPlayer?.Position ?? Vector3.Zero))
                    .FirstOrDefault();

                if (match != null)
                {
                    // Only switch if current ID is 0 or the current target is no longer valid
                    bool currentTargetStillExists = Svc.Objects.Any(x =>
                        x is ICharacter c &&
                        x.ObjectKind == ObjectKind.BattleNpc &&
                        x.GameObjectId == CurrentTargetId &&
                        c.CurrentHp > 0);

                    if (CurrentTargetId == 0)
                    {
                        CurrentTargetId = match.GameObjectId;
                        TargetingHelper.StoredTargetId = match.GameObjectId;
                        TargetingHelper.ResetAutoTargetFlag();
                        Service.PluginLog.Debug($"Set new target ID: {CurrentTargetId}");
                    }
                    else if (match.GameObjectId != CurrentTargetId)
                    {
                        var previousTarget = Svc.Objects
                            .OfType<ICharacter>()
                            .FirstOrDefault(x =>
                                x.ObjectKind == ObjectKind.BattleNpc &&
                                x.GameObjectId == CurrentTargetId);

                        if (previousTarget == null || previousTarget.CurrentHp == 0)
                        {
                            Service.PluginLog.Debug($"Previous target {CurrentTargetId} gone or dead. Checking for duplicate registration.");

                            if (CurrentTargetId != 0 && !RegisteredKills.Contains(CurrentTargetId))
                            {
                                RegisteredKills.Add(CurrentTargetId);
                                TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");
                            }
                            else
                            {
                                Service.PluginLog.Debug($"Skipping duplicate or zero-ID kill registration for {CurrentTargetId}.");
                            }
                            CurrentTargetId = 0;
                            CurrentTargetPosition = null;
                            TargetingHelper.StoredTargetId = 0;
                            TargetingHelper.ResetAutoTargetFlag();
                        }
                        else
                        {
                            Service.PluginLog.Debug($"Previous target {CurrentTargetId} still alive. Not registering kill.");
                        }
                        Service.PluginLog.Debug($"Switching target from {previousId} to {match.GameObjectId}.");
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
                        Service.PluginLog.Debug($"Lost sight of {CurrentTarget}, checking for kill...");

                        if (CurrentTargetId != 0 && !RegisteredKills.Contains(CurrentTargetId))
                        {
                            RegisteredKills.Add(CurrentTargetId);
                            TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");
                        }
                        else
                        {
                            Service.PluginLog.Debug($"Skipping duplicate or zero-ID kill registration for {CurrentTargetId}.");
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
