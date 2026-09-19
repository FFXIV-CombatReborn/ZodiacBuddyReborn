using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.Stages.Animus;
using ZodiacBuddy.Stages.Brave;
using ZodiacBuddy.Stages.Novus;
using ECommons;
using ECommons.DalamudServices;

namespace ZodiacBuddy;

/// <summary>
/// Main plugin implementation.
/// </summary>
public sealed class ZodiacBuddyPlugin : IDalamudPlugin {
    private const string Command = "/pzodiac";
    private const string TargetWindowCommand = "/ztarget";
#if DEBUG
    private const string FateDebugCommand = "/zfate";
    private const string LeveDebugCommand = "/zleve";
    private const string FateGrinderCommand = "/zgrind";
#endif

    private readonly NovusManager novusManager;
    private readonly BraveManager braveManager;
    private readonly WindowSystem windowSystem;
    internal TargetInfoWindow TargetWindow;

    private readonly ConfigWindow configWindow;
    private readonly AnimusManager animus;
    private readonly AnimusAutomationFacade automation;
#if DEBUG
    private readonly FateDebugWindow fateDebugWindow;
    private readonly FateGrinderWindow fateGrinderWindow;
    private readonly FateGrinderMultiPullWindow fateGrinderMultiPullWindow;
    private readonly LeveDebugWindow leveDebugWindow;
#endif
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
        Service.Interface.UiBuilder.OpenConfigUi += this.OnOpenConfigUi;

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
        this.novusManager = new NovusManager();
        this.braveManager = new BraveManager();
        this.animus = new AnimusManager();
        this.automation = new AnimusAutomationFacade(animus);
        TargetWindow = new TargetInfoWindow(automation);
        windowSystem.AddWindow(TargetWindow);
#if DEBUG
        this.windowSystem.AddWindow(this.fateDebugWindow = new FateDebugWindow(automation));
        Service.CommandManager.AddHandler(FateDebugCommand, new CommandInfo(OnFateDebugCommand)
        {
            HelpMessage = "Open the Animus FATE automation test harness.",
            ShowInHelp = true,
        });
        this.windowSystem.AddWindow(this.fateGrinderMultiPullWindow = new FateGrinderMultiPullWindow());
        this.windowSystem.AddWindow(this.fateGrinderWindow = new FateGrinderWindow(automation, fateGrinderMultiPullWindow));
        Service.CommandManager.AddHandler(FateGrinderCommand, new CommandInfo(OnFateGrinderCommand)
        {
            HelpMessage = "Open the ZodiacBuddy FATE grinder.",
            ShowInHelp = true,
        });
        this.windowSystem.AddWindow(this.leveDebugWindow = new LeveDebugWindow(automation));
        Service.CommandManager.AddHandler(LeveDebugCommand, new CommandInfo(OnLeveDebugCommand)
        {
            HelpMessage = "Open the Animus leve test harness. Optional: /zleve <LeveId>.",
            ShowInHelp = true,
        });
#endif
        AutoDutyIpc.Init();
        RSRIPC.Init();
        BossModIPC.Init();
        Service.Interface.UiBuilder.Draw += this.windowSystem.Draw;
    }

    /// <inheritdoc/>
    public void Dispose() {
        Service.Interface.UiBuilder.Draw -= this.windowSystem.Draw;
        Svc.Framework.Update -= animus.WaitForBetweenAreasAndExecute;
        animus.Dispose();
        Service.CommandManager.RemoveHandler(Command);
        TargetWindow.Dispose();
        windowSystem.RemoveWindow(TargetWindow);
        Service.CommandManager.RemoveHandler(TargetWindowCommand);
#if DEBUG
        Service.CommandManager.RemoveHandler(FateDebugCommand);
        windowSystem.RemoveWindow(fateDebugWindow);
        Service.CommandManager.RemoveHandler(FateGrinderCommand);
        windowSystem.RemoveWindow(fateGrinderWindow);
        windowSystem.RemoveWindow(fateGrinderMultiPullWindow);
        Service.CommandManager.RemoveHandler(LeveDebugCommand);
        windowSystem.RemoveWindow(leveDebugWindow);
#endif
        Service.Interface.UiBuilder.OpenConfigUi -= this.OnOpenConfigUi;

        this.novusManager.Dispose();
        this.braveManager.Dispose();
        Service.BonusLightManager.Dispose();
        ECommons.ECommonsMain.Dispose();
    }
#if DEBUG
    internal void OpenFateGrinderWindow()
        => fateGrinderWindow.IsOpen = true;

    internal void OpenFateDebugWindow()
        => fateDebugWindow.IsOpen = true;

    private void OnFateDebugCommand(string command, string arguments)
        => fateDebugWindow.IsOpen = true;

    private void OnLeveDebugCommand(string command, string arguments)
    {
        if (uint.TryParse(arguments.Trim(), out var leveId))
            leveDebugWindow.SelectLeve(leveId);
        leveDebugWindow.IsOpen = true;
    }

    private void OnFateGrinderCommand(string command, string arguments)
        => fateGrinderWindow.IsOpen = true;
#endif

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
            .AddUiForeground("[ZodiacBuddyReborn] ", 45)
            .Append(message);

        Service.ChatGui.Print(new XivChatEntry {
            Type = Service.Configuration.ChatType,
            Message = sb.BuiltString,
        });
    }

    public void PrintStepProgress(string message)
    {
        if (Service.Configuration.BraveEchoTarget)
            PrintMessage(message);
    }

    public void PrintStepProgress(SeString message)
    {
        if (Service.Configuration.BraveEchoTarget)
            PrintMessage(message);
    }

    /// <summary>
    /// Print an error message.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public static void PrintError(string message)
        => Service.ChatGui.PrintError($"[ZodiacBuddyReborn] {message}");
    private void OnOpenConfigUi()
        => this.configWindow.IsOpen = true;

    private void OnCommand(string command, string arguments)
        => this.configWindow.IsOpen = true;
}
