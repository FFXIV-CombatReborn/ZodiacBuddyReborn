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

        public void UpdateCurrentTargetInfo()
        {
            // Step 1: If a target name was manually set, track by that name
            if (!string.IsNullOrEmpty(CurrentTarget))
            {
                // Step 2: Check if the stored GameObjectId is still valid
                if (CurrentTargetId != 0)
                {
                    var stillExists = Svc.Objects
                        .Any(obj => obj is IBattleChara bc && bc.GameObjectId == CurrentTargetId);

                    if (!stillExists)
                    {
                        // Target has despawned or died — clear it
                        CurrentTargetId = 0;
                        TargetingHelper.StoredTargetId = 0;
                    }
                }

                // Step 3: If no valid GameObjectId, try to find another enemy with the same name
                if (CurrentTargetId == 0)
                {
                    var matchingNpc = Svc.Objects
                        .FirstOrDefault(obj => obj.ObjectKind == ObjectKind.BattleNpc &&
                                               obj.Name.TextValue.Equals(CurrentTarget, StringComparison.OrdinalIgnoreCase));

                    if (matchingNpc != null)
                    {
                        CurrentTargetId = matchingNpc.GameObjectId;
                        TargetingHelper.StoredTargetId = matchingNpc.GameObjectId;
                    }
                }

                return; // Done — we either updated or couldn't find a new match yet
            }

            // Step 4: Fallback to current in-game target if no name has been set manually
            var target = Svc.Targets.Target;
            if (target != null && target.ObjectKind == ObjectKind.BattleNpc)
            {
                CurrentTarget = SmartCaseHelper.SmartTitleCase(target.Name.TextValue.Trim());
                CurrentTargetId = target.GameObjectId;
                TargetingHelper.StoredTargetId = target.GameObjectId;
            }
        }

        public override void Draw()
        {
            UpdateCurrentTargetInfo();
            if (string.IsNullOrWhiteSpace(CurrentTarget))
            {
                ImGui.Text("No target selected.");
            }
            else
            {
                ImGui.Text($"Current Target: {CurrentTarget}");
                ImGui.Text($"GameObjectId: {CurrentTargetId}");

                if (ImGui.Button("Retarget by ID"))
                {
                    if (!TargetingHelper.TryTargetById(CurrentTargetId))
                    {
                        Service.ChatGui.PrintError($"Could not retarget enemy by ID.");
                    }
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
