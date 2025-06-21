using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Common.Math;
using ImGuiNET;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
                        Service.PluginLog.Debug("Re-enabling RSR due to post-kill aggro.");
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
                    Service.PluginLog.Debug("Combat over after promoted enemies.");
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
                // Retry auto-targeting outside the ID-switch logic.
                TargetingHelper.AutoTargetStoredIdIfVisible();
            }
        }
        
        public void SetTarget(string name, ulong id = 0)
        {
            fallbackSuppressedPermanently = false;
            // Don't allow setting a target while active
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
            fallbackSuppressionUntil = DateTime.Now.AddSeconds(0.5); // Suppress fallback briefly
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
                string command = $"/vnav moveto {pos.X:F3} {pos.Y:F3} {pos.Z:F3}";
                Service.CommandManager.ProcessCommand(command);
                Service.ChatGui.Print($"Pathing to {CurrentTarget} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");

                lastPathingTime = DateTime.Now;
                pendingPathing = false;
            }
            else
            {
                if (DateTime.Now < fallbackSuppressionUntil)
                {
                    Service.PluginLog.Debug("Suppressed fallback pathing to give enemy detection time.");
                    pendingPathing = true;
                    return;
                }

                if (fallbackSuppressedPermanently || TargetingHelper.KillCount >= 3 || Svc.Condition[ConditionFlag.InCombat])
                {;
                    pendingPathing = false;
                    return;
                }

                string fallbackCommand = "/vnav moveflag";
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

                    if (CurrentTargetId == 0 || !currentTargetStillExists)
                    {
                        if (match.GameObjectId != CurrentTargetId)
                        {
                            TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");
                            CurrentTargetId = match.GameObjectId;
                            TargetingHelper.StoredTargetId = match.GameObjectId;
                            TargetingHelper.ResetAutoTargetFlag();
                        }
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

                        TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");

                        CurrentTargetId = 0;
                        CurrentTargetPosition = null;
                        TargetingHelper.StoredTargetId = 0;
                        TargetingHelper.ResetAutoTargetFlag();

                        if (!CompletedObjective)
                            pendingPathing = true;  //Pathing if kill objective not complete
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
