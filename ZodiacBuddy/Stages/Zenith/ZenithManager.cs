using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ZodiacBuddy.Stages.Zenith;

/// <summary>
/// Your buddy for the Zodiac Zenith stage.
/// </summary>
internal class ZenithManager : IDisposable {
    private readonly ZenithWindow window;

    /// <summary>
    /// Item ID for Allagan Tomestone of Poetics (commonly used tomestone in 2025)
    /// </summary>
    private const uint TomestoneOfPoeticsId = 28;

    /// <summary>
    /// Item ID for Thavnairian Mist (used for Zenith upgrades)
    /// </summary>
    private const uint ThavnairianMistId = 6268;

    /// <summary>
    /// Gets the current count of Allagan Tomestones of Poetics
    /// </summary>
    public static unsafe int GetTomestoneCount() {
        return (int)InventoryManager.Instance()->GetInventoryItemCount(TomestoneOfPoeticsId);
    }

    /// <summary>
    /// Gets the current count of Thavnairian Mist
    /// </summary>
    public static unsafe int GetThavnairianMistCount() {
        return (int)InventoryManager.Instance()->GetInventoryItemCount(ThavnairianMistId);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZenithManager"/> class.
    /// </summary>
    public ZenithManager() {
        this.window = new ZenithWindow();
        
        Service.Framework.Update += this.OnUpdate;
        Service.Interface.UiBuilder.Draw += this.window.Draw;
    }

    private static ZenithConfiguration Configuration => Service.Configuration.Zenith;

    /// <inheritdoc/>
    public void Dispose() {
        Service.Framework.Update -= this.OnUpdate;
        Service.Interface.UiBuilder.Draw -= this.window.Draw;
    }

    private void OnUpdate(IFramework framework) {
        try {
            if (!Configuration.DisplayRelicInfo) {
                this.window.ShowWindow = false;
                return;
            }

            var mainhand = Util.GetEquippedItem(0);
            var offhand = Util.GetEquippedItem(1);

            var shouldShowWindow =
                ZenithRelic.Items.ContainsKey(mainhand.ItemId) ||
                ZenithRelic.Items.ContainsKey(offhand.ItemId);

            this.window.ShowWindow = shouldShowWindow;
            this.window.MainHandItem = mainhand;
            this.window.OffhandItem = offhand;
        }
        catch (Exception ex) {
            Service.PluginLog.Error(ex, $"Unhandled error during {nameof(ZenithManager)}.{nameof(this.OnUpdate)}");
        }
    }
}