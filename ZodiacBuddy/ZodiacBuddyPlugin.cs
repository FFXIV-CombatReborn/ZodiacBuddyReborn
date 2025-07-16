using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ZodiacBuddy.TargetWindow;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.Stages.Atma;
using ZodiacBuddy.Stages.Brave;
using ZodiacBuddy.Stages.Novus;
using ZodiacBuddy.Stages.Zenith;
using ECommons;
using ECommons.DalamudServices;

namespace ZodiacBuddy;

/// <summary>
/// Main plugin implementation.
/// </summary>
public sealed class ZodiacBuddyPlugin : IDalamudPlugin {
    private const string Command = "/pzodiac";
    private const string TargetWindowCommand = "/ztarget";

    private readonly AtmaManager animusBuddy;
    private readonly NovusManager novusManager;
    private readonly BraveManager braveManager;
    private readonly ZenithManager zenithManager;
    private readonly WindowSystem windowSystem;
    internal TargetInfoWindow TargetWindow;

    private readonly ConfigWindow configWindow;
    private readonly AtmaManager atma;
    /// <summary>
    /// Initializes a new instance of the <see cref="ZodiacBuddyPlugin"/> class.
    /// </summary>
    /// <param name="pluginInterface">Dalamud plugin interface.</param>
    public ZodiacBuddyPlugin(IDalamudPluginInterface pluginInterface) {
        pluginInterface.Create<Service>();

        ECommons.ECommonsMain.Init(pluginInterface, this, Module.DalamudReflector);
        Service.Plugin = this;
        Service.Configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();

        this.windowSystem = new WindowSystem("ZodiacBuddy");

        this.windowSystem.AddWindow(this.configWindow = new ConfigWindow());
        TargetWindow = new TargetInfoWindow();
        windowSystem.AddWindow(TargetWindow);
        Service.Interface.UiBuilder.OpenConfigUi += this.OnOpenConfigUi;
        Service.Interface.UiBuilder.Draw += this.windowSystem.Draw;

        Service.CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand) {
            HelpMessage = "Open a window to edit various settings.",
            ShowInHelp = true,
        });
        Service.CommandManager.AddHandler(TargetWindowCommand, new CommandInfo(OnTargetWindowCommand)
        {
            HelpMessage = "Open the ZodiacBuddy target tracking window.",
            ShowInHelp = true,
        });

        Service.BonusLightManager = new BonusLightManager();
        this.animusBuddy = new AtmaManager();
        this.novusManager = new NovusManager();
        this.braveManager = new BraveManager();
        this.zenithManager = new ZenithManager();
        this.atma = new AtmaManager();
        AtmaManager.OnFallbackPathIssued = () => atma.EnqueueUnmountAfterNav();

    }

    /// <inheritdoc/>
    public void Dispose() {
        Svc.Framework.Update -= atma.WaitForBetweenAreasAndExecute;
        atma.Dispose();
        Service.CommandManager.RemoveHandler(Command);
        windowSystem.RemoveWindow(TargetWindow);
        Service.CommandManager.RemoveHandler(TargetWindowCommand);
        Service.Interface.UiBuilder.Draw -= this.windowSystem.Draw;
        Service.Interface.UiBuilder.OpenConfigUi -= this.OnOpenConfigUi;

        this.animusBuddy.Dispose();
        this.novusManager.Dispose();
        this.braveManager.Dispose();
        this.zenithManager.Dispose();
        Service.BonusLightManager.Dispose();
        ECommons.ECommonsMain.Dispose();
    }
    private void OnTargetWindowCommand(string command, string arguments)
    {
        TargetWindow.IsOpen = true;
    }
    /// <summary>
    /// Print a message.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public void PrintMessage(SeString message) {
        var sb = new SeStringBuilder()
            .AddUiForeground("[ZodiacBuddy] ", 45)
            .Append(message);

        Service.ChatGui.Print(new XivChatEntry {
            Type = Service.Configuration.ChatType,
            Message = sb.BuiltString,
        });
    }

    /// <summary>
    /// Print an error message.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public static void PrintError(string message)
        => Service.ChatGui.PrintError($"[ZodiacBuddy] {message}");
    private void OnOpenConfigUi()
        => this.configWindow.IsOpen = true;

    private void OnCommand(string command, string arguments)
        => this.configWindow.IsOpen = true;
}