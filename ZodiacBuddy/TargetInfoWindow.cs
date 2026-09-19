using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;
using ZodiacBuddy.Stages.Animus;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy;

internal sealed class TargetInfoWindow : Window
{
    private const byte MonsterObjectiveRequiredProgress = 3;
    private readonly AnimusAutomationFacade automation;

    internal TargetInfoWindow(AnimusAutomationFacade automation) : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.automation = automation;
        IsOpen = Service.Configuration.TargetInfoWindowWasOpen;
    }

    public override void OnOpen()
    {
        Service.Configuration.TargetInfoWindowWasOpen = true;
        Service.Configuration.Save();
    }

    public override void OnClose()
    {
        Service.Configuration.TargetInfoWindowWasOpen = false;
        Service.Configuration.Save();
    }

    public void Dispose()
    {
        Service.Configuration.TargetInfoWindowWasOpen = IsOpen;
        Service.Configuration.Save();
    }

    public override void Draw()
    {
        var atmaEnabled = Service.Configuration.IsAtmaManagerEnabled;
        if (ImGui.Checkbox("Enable Atma Manager", ref atmaEnabled))
        {
            Service.Configuration.IsAtmaManagerEnabled = atmaEnabled;
            Service.Configuration.Save();
        }

        ImGui.Separator();

        var bookAutomation = automation.GetBookAutomationSnapshot();
        var enemyAutomation = automation.GetEnemyAutomationSnapshot();
        var leveAutomation = automation.GetLeveAutomationSnapshot();
        if (bookAutomation.State == BookAutomationState.Running)
        {
            if (ImGui.Button("Stop Book"))
                automation.CancelBookAutomation();
        }
        else
        {
            if (ImGui.Button("Start Book"))
                automation.StartBookAutomation();
        }

        if (bookAutomation.State != BookAutomationState.Idle)
        {
            DrawBookAutomationSummary(bookAutomation, enemyAutomation);
            return;
        }

        ImGui.Separator();

        if (IsLeveAutomationActive(leveAutomation.State))
        {
            var leveLabel = !string.IsNullOrWhiteSpace(leveAutomation.TargetName)
                ? leveAutomation.TargetName
                : $"LeveId={leveAutomation.LeveId}";
            ImGui.Text($"Objective: Leve - {leveLabel}");
            if (!string.IsNullOrWhiteSpace(leveAutomation.Status))
                ImGui.TextWrapped($"Activity: {leveAutomation.Status}");
            return;
        }

        if (enemyAutomation.State == EnemyAutomationState.Idle || string.IsNullOrWhiteSpace(enemyAutomation.TargetName))
        {
            ImGui.Text("No objective selected.");
            return;
        }

        ImGui.Text($"Objective: Enemy - {enemyAutomation.TargetName}");
        DrawMonsterProgress(enemyAutomation);
        DrawNavigationStatus(enemyAutomation.NavigationState);
    }

    private static void DrawBookAutomationSummary(BookAutomationSnapshot bookAutomation, EnemyAutomationSnapshot enemyAutomation)
    {
        var overallProgress = bookAutomation.TotalObjectives > 0
            ? $"{bookAutomation.CompletedObjectives} / {bookAutomation.TotalObjectives}"
            : "Unknown";

        ImGui.Text(bookAutomation.BookName);
        ImGui.Text($"Overall: {overallProgress}");

        if (!string.IsNullOrWhiteSpace(bookAutomation.CurrentObjective))
        {
            ImGui.Text($"Objective: {FormatBookObjective(bookAutomation.CurrentObjective)}");

            if (bookAutomation.CurrentObjectiveKind == BookObjectiveKind.Enemy)
            {
                DrawMonsterProgress(enemyAutomation);
                DrawNavigationStatus(enemyAutomation.NavigationState);
            }
            else if (bookAutomation.State == BookAutomationState.Running && !string.IsNullOrWhiteSpace(bookAutomation.Status))
            {
                ImGui.TextWrapped($"Activity: {bookAutomation.Status}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(bookAutomation.Status))
        {
            ImGui.TextWrapped($"Status: {bookAutomation.Status}");
        }
    }

    private static void DrawMonsterProgress(EnemyAutomationSnapshot enemyAutomation)
    {
        if (enemyAutomation.BookProgress is not byte bookProgress)
            return;

        var complete = bookProgress >= MonsterObjectiveRequiredProgress;
        var bookColor = complete ? new Vector4(0f, 1f, 0f, 1f) : new Vector4(1f, 1f, 1f, 1f);
        ImGui.TextColored(bookColor, complete ? "Progress: Complete" : $"Progress: {bookProgress} / {MonsterObjectiveRequiredProgress}");

        if (enemyAutomation.PendingCredits > 0 && enemyAutomation.WaitingForBookCredit)
            ImGui.Text($"Waiting for book credit: {enemyAutomation.PendingCredits}");
    }

    private static bool IsLeveAutomationActive(LeveAutomationState state)
        => state is not (LeveAutomationState.Idle
            or LeveAutomationState.Complete
            or LeveAutomationState.Unavailable
            or LeveAutomationState.Failed);

    private static string FormatBookObjective(string objective)
    {
        var separator = objective.IndexOf(':');
        if (separator <= 0 || separator >= objective.Length - 1)
            return objective;

        return $"{objective[..separator]} - {objective[(separator + 1)..].TrimStart()}";
    }

    private static void DrawNavigationStatus(EnemyNavigationDisplayState navigationState)
    {
        var (status, color) = navigationState switch
        {
            EnemyNavigationDisplayState.NavmeshNotReady => ("Navmesh Not Ready", new Vector4(1f, 0f, 0f, 1f)),
            EnemyNavigationDisplayState.GeneratingPath => ("Generating Path...", new Vector4(1f, 1f, 0f, 1f)),
            EnemyNavigationDisplayState.Pathing => ("Pathing", new Vector4(0f, 1f, 0f, 1f)),
            _ => (null, default(Vector4)),
        };

        if (status != null)
            ImGui.TextColored(color, $"Navigation: {status}");
    }
}
