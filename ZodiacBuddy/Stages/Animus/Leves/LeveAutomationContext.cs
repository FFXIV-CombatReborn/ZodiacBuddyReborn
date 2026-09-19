using System;
using System.Collections.Generic;
using System.Numerics;
using ZodiacBuddy.Stages.Animus.Data;
using ZodiacBuddy.Systems.Leves;

namespace ZodiacBuddy.Stages.Animus;

internal sealed class LeveAutomationContext
{
    internal LeveObjectiveDefinition AutomationTarget;
    internal LeveAutomationState AutomationState;
    internal long AutomationRunId;
    internal uint BookId;
    internal bool DebugMode;
    internal bool RerollMode;
    internal bool AllowUnavailableReturn;
    internal DateTime CreditDeadline = DateTime.MinValue;
    internal string AutomationStatus = "Idle";
    internal List<Vector3> VisitedMarkers { get; } = [];
    internal DateTime NoObjectiveSince = DateTime.MinValue;
    internal DateTime NextMarkerSweepAt = DateTime.MinValue;
    internal ulong CombatTargetId;
    internal Vector3? FieldNavigationDestination;
    internal DateTime AcquireCompletedAt = DateTime.MinValue;
    internal DateTime InitiateRequestedAt = DateTime.MinValue;
    internal DateTime RewardCompletedAt = DateTime.MinValue;
    internal ulong EscortActorId;
    internal Vector3 StartMarker;
    internal Vector3? EscortGoal;
    internal DateTime LastBeckonAt = DateTime.MinValue;
    internal DateTime EscortFinishReachedAt = DateTime.MinValue;
    internal ulong EscortMotionActorId;
    internal Vector3 EscortMotionPosition;
    internal DateTime EscortMotionSampleAt = DateTime.MinValue;
    internal DateTime EscortLastMovedAt = DateTime.MinValue;
    internal bool EscortMovementLogged;
    internal bool EscortLeadNavigationActive;
    internal bool StartAwaitingDismount;
    internal DateTime StartDismountRequestedAt = DateTime.MinValue;
    internal int StartLandingRecoveryAttempts;
    internal DateTime IssuerDismountRequestedAt = DateTime.MinValue;
    internal int IssuerLandingRecoveryAttempts;
    internal DateTime LastParchmentReadAt = DateTime.MinValue;
    internal DateTime LastEventItemUseAt = DateTime.MinValue;
    internal ulong LurePrimeObjectId;
    internal DateTime LurePrimeInRangeAt = DateTime.MinValue;
    internal bool TimedCullWaiting;
    internal string TimedCullTodoSnapshot = string.Empty;
}
