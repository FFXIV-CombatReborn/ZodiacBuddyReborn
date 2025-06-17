using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
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
        public string? CurrentTarget;
        public ulong CurrentTargetId;
        public bool IsPathing => VNavmesh.Path.IsRunning();
        private bool pendingPathing = false;
        private DateTime lastPathingTime = DateTime.MinValue;


        public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.IsOpen = true;
        }

        // Call this to set the target name from BraveBook click or similar
        public void SetTarget(string name, ulong id = 0)
        {
            CurrentTarget = SmartCaseHelper.SmartTitleCase(name);
            CurrentTargetId = id;

            if (id != 0)
                TargetingHelper.StoredTargetId = id;
        }

        public Vector3? CurrentTargetPosition { get; private set; }

        private void StartPathingToCurrentTarget()
        {
            if (VNavmesh.Path.IsRunning())
            {
                Service.ChatGui.Print("Already pathing. Will retry after current path completes.");
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
                string fallbackCommand = "/vnav moveflag";
                Service.CommandManager.ProcessCommand(fallbackCommand);
                Service.ChatGui.Print("No enemy found nearby. Pathing to map flag.");

                lastPathingTime = DateTime.Now;
                pendingPathing = false;
            }
        }

        public void UpdateCurrentTargetInfo()
        {
            if (!string.IsNullOrEmpty(CurrentTarget))
            {
                // Store previous ID to detect a change
                var previousId = CurrentTargetId;

                var match = Svc.Objects
                    .Where(obj => obj.ObjectKind == ObjectKind.BattleNpc &&
                                  obj.Name.TextValue.Equals(CurrentTarget, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(obj => Vector3.Distance(obj.Position, Svc.ClientState.LocalPlayer?.Position ?? Vector3.Zero))
                    .FirstOrDefault();

                if (match != null)
                {
                    if (match.GameObjectId != CurrentTargetId)
                    {
                        // New enemy found
                        CurrentTargetId = match.GameObjectId;
                        CurrentTargetPosition = match.Position;

                        TargetingHelper.StoredTargetId = CurrentTargetId;
                        StartPathingToCurrentTarget(); // 🔥 auto-path on change
                    }
                }
                else
                {
                    // Enemy not found, clear ID and coords
                    if (CurrentTargetId != 0 || CurrentTargetPosition != null)
                    {
                        CurrentTargetId = 0;
                        CurrentTargetPosition = null;
                        StartPathingToCurrentTarget(); // 🔁 fallback to map flag
                    }
                }

                return;
            }

            // No custom target from book, use in-game target as fallback
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
            if (pendingPathing && !VNavmesh.Path.IsRunning() && (DateTime.Now - lastPathingTime).TotalSeconds > 2)
            {
                StartPathingToCurrentTarget();
            }
            UpdateCurrentTargetInfo();
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

                if (ImGui.Button("Retarget by ID"))
                {
                    if (!TargetingHelper.TryTargetById(CurrentTargetId))
                    {
                        Service.ChatGui.PrintError($"Could not retarget enemy by ID.");
                    }
                }
                if (ImGui.Button("Path to Target"))
                {
                    StartPathingToCurrentTarget();
                }
                ImGui.SameLine();

                if (ImGui.Button("Retarget by Name"))
                {
                    if (!string.IsNullOrEmpty(CurrentTarget) && !TargetingHelper.TryTargetByName(CurrentTarget))
                    {
                        Service.ChatGui.PrintError($"Could not retarget enemy named '{CurrentTarget}'.");
                    }
                }
            }

            ImGui.Separator();

            // Status
            Vector4 color;
            string status;

            if (!VNavmesh.Nav.IsReady())
            {
                status = "Navmesh Not Ready";
                color = new Vector4(1f, 0f, 0f, 1f); // Red
            }
            else if (VNavmesh.Nav.PathfindInProgress())
            {
                status = "Generating Path...";
                color = new Vector4(1f, 1f, 0f, 1f); // Yellow
            }
            else if (VNavmesh.Path.IsRunning())
            {
                status = "Pathing";
                color = new Vector4(0f, 1f, 0f, 1f); // Green
            }
            else
            {
                status = "Idle";
                color = new Vector4(1f, 1f, 1f, 1f); // White
            }

            ImGui.TextColored(color, $"Status: {status}");
        }


    }
}
