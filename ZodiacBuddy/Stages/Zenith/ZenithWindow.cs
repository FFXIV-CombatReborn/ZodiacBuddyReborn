using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiNET;
using ZodiacBuddy.InformationWindow;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Zenith information window.
/// </summary>
public class ZenithWindow() : InformationWindow.InformationWindow("Zodiac Zenith Information") {
    private static InformationWindowConfiguration InfoWindowConfiguration => Service.Configuration.InformationWindow;
    
    /// <summary>
    /// Reference to the ZenithManager for navigation functionality.
    /// </summary>
    public ZenithManager? Manager { get; set; }

    /// <inheritdoc/>
    protected override void DisplayRelicInfo(InventoryItem item) {
        if (!ZenithRelic.Items.TryGetValue(item.ItemId, out var name))
            return;

        name = name
            .Replace("Œ", "Oe")
            .Replace("œ", "oe");
        ImGui.Text(name);

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, InfoWindowConfiguration.ProgressColor);

        // Display upgrade information
        ImGui.Text("Ready to upgrade to Zenith (Item Level 90)");
        ImGui.Text("After Zenith: Atma Stage");
        
        // Show upgrade instructions
        if (Service.Configuration.Zenith.ShowMaterialRequirements) {
            ImGui.Separator();
            ImGui.Text("Requirements to upgrade to Zenith:");
            
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
            
            ImGui.Text("• Bring materials to The Furnace in North Shroud (Hyrstmill)");
            
            // Navigation buttons
            if (Service.Configuration.Zenith.EnableNavigation) {
                ImGui.Separator();
                ImGui.Text("Navigation:");
                
                if (ImGui.Button("Go to Furnace for Upgrade##ZenithFurnace")) {
                    Manager?.NavigateToLocation(ZenithDestinations.Furnace);
                }
                ImGui.SameLine();
                if (ImGui.Button("Stop Navigation##ZenithStop")) {
                    Manager?.StopNavigation();
                }
                
                // Vendor navigation for materials
                if (mistCount < requiredMists) {
                    ImGui.Spacing();
                    ImGui.Text("Get Thavnairian Mist:");
                    if (ImGui.Button("Auriana (Mor Dhona)##ZenithAuriana")) {
                        Manager?.NavigateToLocation(ZenithDestinations.AurianaPoeticsVendor);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Hismena (Idyllshire)##ZenithHismena")) {
                        Manager?.NavigateToLocation(ZenithDestinations.HismenaPoeticsVendor);
                    }
                }
            }
        }

        ImGui.PopStyleColor();
    }
}