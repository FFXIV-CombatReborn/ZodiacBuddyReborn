using System.Collections.Generic;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class AnimusAutomationFacade
{
    private readonly AnimusManager animus;

    internal AnimusAutomationFacade(AnimusManager animus)
    {
        this.animus = animus;
    }

    internal BookAutomationSnapshot GetBookAutomationSnapshot()
        => animus.GetBookAutomationSnapshot();

    internal EnemyAutomationSnapshot GetEnemyAutomationSnapshot()
        => animus.GetEnemyAutomationSnapshot();

    internal bool StartBookAutomation()
        => animus.StartBookAutomation();

    internal void CancelBookAutomation()
        => animus.CancelBookAutomation();

    internal IReadOnlyList<FateObjectiveDefinition> GetDebugFateTargets()
        => animus.GetDebugFateTargets();

    internal FateAutomationSnapshot GetFateDebugSnapshot()
        => animus.GetFateAutomationSnapshot();

    internal bool StartDebugFate(uint fateId)
        => animus.StartDebugFate(fateId);

    internal bool StartDebugFateWithGrinding(uint fateId)
        => animus.StartDebugFateWithGrinding(fateId);

    internal bool StartGeneralFateGrinding()
        => animus.StartGeneralFateGrinding();

    internal bool StartDebugLiveFate(uint fateId)
        => animus.StartDebugLiveFate(fateId);

    internal FateGrindingSnapshot GetFateGrindingSnapshot()
        => animus.GetFateGrindingSnapshot();

    internal IReadOnlyList<FateRuntimeDiagnostic> GetLiveFateDiagnostics()
        => animus.GetLiveFateDiagnostics();

    internal void StopFateGrinding()
        => animus.StopFateGrinding();

    internal void StopGeneralFateGrinding()
        => animus.StopGeneralFateGrinding();

    internal void CancelFateDebugRun()
        => animus.CancelFateDebugRun();

    internal IReadOnlyList<LeveObjectiveDefinition> GetDebugLeveTargets()
        => animus.GetDebugLeveTargets();

    internal bool StartDebugLeveTravel(uint leveId)
        => animus.StartDebugLeveTravel(leveId);

    internal LeveAutomationSnapshot GetLeveAutomationSnapshot()
        => animus.GetLeveAutomationSnapshot();

    internal bool StartDebugLeveAutomation(uint leveId)
        => animus.StartDebugLeveAutomation(leveId);

    internal void CancelDebugLeveAutomation()
        => animus.CancelDebugLeveAutomation();
}
