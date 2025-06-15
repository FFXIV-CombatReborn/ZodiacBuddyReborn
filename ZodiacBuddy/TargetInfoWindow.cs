using System.Globalization;
using Dalamud.Interface.Windowing;
using System.Text.RegularExpressions;
using ImGuiNET;
using FFXIVClientStructs.FFXIV.Common.Math;
using ZodiacBuddy;

namespace ZodiacBuddy;

internal class TargetInfoWindow : Window
{
    public bool IsPathing => VNavmesh.Path.IsRunning();
    public string? CurrentTarget;

    public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
    {

        this.IsOpen = true; // TODO: Add Toggle
    }

    public void SetTarget(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        CurrentTarget = Util.SmartTitleCase(name.Trim());
    }

    public override void Draw()
    {

        if (string.IsNullOrWhiteSpace(CurrentTarget))
        {
            ImGui.Text("No target selected.");
        }
        else
        {
            ImGui.Text($"Current Target: {CurrentTarget}");
        }

        ImGui.Separator();
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

    // TODO:
    // ImGui.Separator();
    // ImGui.Text("Status: Idle");
    // if (ImGui.Button("Force Retarget")) { /* logic here */ }
}

