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
        public string? CurrentTarget { get; private set; }
        public ulong CurrentTargetId { get; private set; }
        public bool IsPathing => VNavmesh.Path.IsRunning();
        
        

        public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.IsOpen = true;
        }

        // Call this to set the target name from BraveBook click or similar
        public void SetTarget(string targetName)
        {
            CurrentTarget = targetName;
            CurrentTargetId = 0; // reset id, will update after lookup
        }

        public void UpdateCurrentTargetInfo()
        {
            // Do nothing if we already have a name but are waiting for the enemy to appear
            if (!string.IsNullOrEmpty(CurrentTarget) && CurrentTargetId == 0)
            {
                // Try to find matching NPC to get GameObjectId
                var matchingNpc = Svc.Objects
                    .Where(obj => obj.ObjectKind == ObjectKind.BattleNpc &&
                                  obj.Name.TextValue.Equals(CurrentTarget, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (matchingNpc != null)
                {
                    CurrentTargetId = matchingNpc.GameObjectId;
                }

                return; // Keep showing the name even if enemy isn't found yet
            }

            // If no name has been set from book, fallback to actual in-game target
            var target = Svc.Targets.Target;
            if (target != null && target.ObjectKind == ObjectKind.BattleNpc)
            {
                CurrentTarget = SmartCaseHelper.SmartTitleCase(target.Name.TextValue.Trim());
                CurrentTargetId = target.GameObjectId;
            }
        }
        public override void Draw()
        {
            UpdateCurrentTargetInfo();

            if (!string.IsNullOrEmpty(CurrentTarget))
            {
                ImGui.Text($"Current Target: {CurrentTarget}");

                if (CurrentTargetId != 0)
                    ImGui.Text($"GameObjectId: {CurrentTargetId}");
                else
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "Waiting for enemy to appear...");

                if (ImGui.Button("Retarget by Name"))
                {
                    bool success = TargetingHelper.TryTargetByName(CurrentTarget!); // Null-forgiving operator used safely here
                    if (!success)
                    {
                        Service.ChatGui.PrintError($"Could not retarget enemy named '{CurrentTarget}'.");
                    }
                }
            }
            else
            {
                ImGui.Text("No target selected.");
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
        }

    }
}
