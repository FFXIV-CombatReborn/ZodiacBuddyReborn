using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiNET;
using ZodiacBuddy.InformationWindow;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Zenith information window.
/// </summary>
public class ZenithWindow() : InformationWindow.InformationWindow("Zodiac Zenith Information") {
    private static InformationWindowConfiguration InfoWindowConfiguration => Service.Configuration.InformationWindow;

    /// <inheritdoc/>
    protected override void DisplayRelicInfo(InventoryItem item) {
        if (!ZenithRelic.Items.TryGetValue(item.ItemId, out var name))
            return;

        name = name
            .Replace("Œ", "Oe")
            .Replace("œ", "oe");
        ImGui.Text(name);

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, InfoWindowConfiguration.ProgressColor);

        // Display basic information about the Zenith weapon
        ImGui.Text("Zenith Stage - Item Level 90");
        ImGui.Text("Next: Atma Stage");
        
        // Show upgrade instructions
        if (Service.Configuration.Zenith.ShowMaterialRequirements) {
            ImGui.Separator();
            ImGui.Text("Upgrade Requirements:");
            
            var tomestoneCount = ZenithManager.GetTomestoneCount();
            var mistCount = ZenithManager.GetThavnairianMistCount();
            var requiredMists = 3;
            
            // Display Thavnairian Mist requirement with current count
            if (mistCount >= requiredMists) {
                ImGui.PushStyleColor(ImGuiCol.Text, 0xFF00FF00); // Green
                ImGui.Text($"✓ 3x Thavnairian Mist ({mistCount}/{requiredMists})");
                ImGui.PopStyleColor();
            } else {
                ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red
                ImGui.Text($"• 3x Thavnairian Mist ({mistCount}/{requiredMists})");
                ImGui.PopStyleColor();
                
                // Show tomestone count if we need to buy more mists
                var neededMists = requiredMists - mistCount;
                var neededTomestones = neededMists * 20;
                
                if (tomestoneCount >= neededTomestones) {
                    ImGui.PushStyleColor(ImGuiCol.Text, 0xFF00FFFF); // Yellow
                    ImGui.Text($"  ✓ {neededTomestones} Tomestones of Poetics ({tomestoneCount} available)");
                    ImGui.PopStyleColor();
                } else {
                    ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red
                    ImGui.Text($"  Need {neededTomestones} Tomestones of Poetics ({tomestoneCount}/{neededTomestones})");
                    ImGui.PopStyleColor();
                }
            }
            
            ImGui.Text("• Visit Furnace in Central Thanalan");
        }

        ImGui.PopStyleColor();
    }
}