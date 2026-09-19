using System;
using System.Collections.Generic;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Fates;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class BookPlannerState
{
    internal BookAutomationState AutomationState = BookAutomationState.Idle;
    internal uint AutomationBookId;
    internal string AutomationBookName = string.Empty;
    internal string AutomationStatus = "Idle.";
    internal BookObjectiveKind CurrentObjectiveKind = BookObjectiveKind.None;
    internal IBraveObjectiveDefinition? CurrentObjective;
    internal int CompletedObjectives;
    internal int TotalObjectives;
    internal HashSet<int> FateProbedSlots { get; } = [];
    internal HashSet<uint> UnavailableLeveIds { get; } = [];
    internal HashSet<uint> UnavailableRerollLeveIds { get; } = [];
    internal bool FateSweepPending;
    internal bool CurrentFateProbe;
    internal bool CurrentLeveReroll;
    internal int EnemyBatchCompleted;
    internal uint EnemyBatchTerritory;
    internal int LeveOfferGeneration = 1;
    internal string LastBlockedLeveIssuer = string.Empty;
    internal DateTime FateTerritoryDwellUntil = DateTime.MinValue;
    internal string PlannerBlockReason = string.Empty;
    internal bool FateGrindingActive;
    internal ushort FateGrindingFillerId;
    internal int FateGrindingAttempts;
    internal string FateGrindingStatus = string.Empty;
    internal FateExecutionResult FateGrindingLastResult = FateExecutionResult.None;
    internal Dictionary<ushort, DateTime> FateGrindingBackoffUntil { get; } = [];
}
