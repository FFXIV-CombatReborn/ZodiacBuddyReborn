using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class FateGrinderMultiPullWindow : Window
{
    internal FateGrinderMultiPullWindow() : base("ZodiacBuddy Multi-Pull Configuration")
    {
        Size = new Vector2(430f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void Draw()
    {
        var config = Service.Configuration.FateGrinder;

        ImGui.Text("Local pull tuning");
        ImGui.TextDisabled("RSR keeps target/action ownership; ZBR only performs short local pull nudges.");
        ImGui.Spacing();

        var radius = config.MultiPullRadius;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("Pull radius (y)", ref radius, 1, 2))
        {
            config.MultiPullRadius = Math.Clamp(radius, FateGrinderMultiPullProfiles.MinimumPullRadius, FateGrinderMultiPullProfiles.MaximumPullRadius);
            Service.Configuration.Save();
        }

        var hpFloor = config.MultiPullHpFloorPercent;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("Stop below HP (%)", ref hpFloor, 5, 10))
        {
            config.MultiPullHpFloorPercent = Math.Clamp(hpFloor, FateGrinderMultiPullProfiles.MinimumHpFloorPercent, FateGrinderMultiPullProfiles.MaximumHpFloorPercent);
            Service.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Target pack size by job");

        if (ImGui.BeginChild("MultiPullJobs", new Vector2(0f, -42f), true))
        {
            FateGrinderCombatRole? currentRole = null;
            foreach (var job in FateGrinderMultiPullProfiles.Jobs)
            {
                if (currentRole != job.Role)
                {
                    currentRole = job.Role;
                    ImGui.Spacing();
                    ImGui.Text(job.Role switch
                    {
                        FateGrinderCombatRole.PhysicalRanged => "Physical Ranged",
                        _ => job.Role.ToString(),
                    });
                }

                ImGui.PushID((int)job.ClassJobId);
                ImGui.Text(job.Abbreviation);
                ImGui.SameLine(85f);
                var pack = FateGrinderMultiPullProfiles.GetPackSize(config, job.ClassJobId);
                ImGui.SetNextItemWidth(90f);
                if (ImGui.InputInt("##PackSize", ref pack, 1, 1))
                {
                    FateGrinderMultiPullProfiles.SetPackSize(config, job.ClassJobId, pack);
                    Service.Configuration.Save();
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"default {job.DefaultPackSize}");
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();
        if (ImGui.Button("Reset Pull Defaults"))
        {
            config.ResetMultiPullDefaults();
            Service.Configuration.Save();
        }
    }
}
