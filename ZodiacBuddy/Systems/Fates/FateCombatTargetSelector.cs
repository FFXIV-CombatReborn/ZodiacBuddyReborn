using Dalamud.Game.ClientState.Objects.Types;
using System.Numerics;

namespace ZodiacBuddy.Systems.Fates;

internal readonly record struct FateThreatSelection(
    IBattleNpc? Target,
    int DirectThreatCount,
    int BuddyThreatCount,
    int IgnoredActiveFateThreatCount,
    bool ExternalOnly,
    bool Active,
    bool Entered,
    bool Exited,
    bool TargetChanged,
    bool ThreatCountChanged);

internal sealed class FateCombatTargetSelector
{
    private const int ThreatClearEnterCount = 2;

    private bool _threatClearActive;
    private ulong _committedThreatTargetId;
    private int _lastDirectThreatCount;
    private int _lastBuddyThreatCount;
    private int _lastIgnoredActiveFateThreatCount;

    internal bool ThreatClearActive => _threatClearActive;

    internal FateThreatSelection SelectDirectThreat(ushort activeFateId, Vector3 fateCenter, float fateRadius, bool excludeActiveFateThreats = false)
    {
        var previousTargetId = _committedThreatTargetId;
        var previousDirectThreatCount = _lastDirectThreatCount;
        var previousBuddyThreatCount = _lastBuddyThreatCount;
        var previousIgnoredActiveFateThreatCount = _lastIgnoredActiveFateThreatCount;
        var target = FateTargeting.GetBestDirectThreatTarget(
            activeFateId,
            _threatClearActive ? _committedThreatTargetId : 0,
            fateCenter,
            fateRadius,
            excludeActiveFateThreats,
            out var directThreatCount,
            out var buddyThreatCount,
            out var ignoredActiveFateThreatCount,
            out var committedTargetStillValid);
        var threatCountChanged = directThreatCount != previousDirectThreatCount
            || buddyThreatCount != previousBuddyThreatCount
            || ignoredActiveFateThreatCount != previousIgnoredActiveFateThreatCount;
        _lastDirectThreatCount = directThreatCount;
        _lastBuddyThreatCount = buddyThreatCount;
        _lastIgnoredActiveFateThreatCount = ignoredActiveFateThreatCount;

        if (!_threatClearActive)
        {
            if (directThreatCount < ThreatClearEnterCount || target == null)
                return new(null, directThreatCount, buddyThreatCount, ignoredActiveFateThreatCount, excludeActiveFateThreats, false, false, false, false, threatCountChanged);

            _threatClearActive = true;
            _committedThreatTargetId = target.GameObjectId;
            return new(target, directThreatCount, buddyThreatCount, ignoredActiveFateThreatCount, excludeActiveFateThreats, true, true, false, true, threatCountChanged);
        }

        if (committedTargetStillValid && target != null)
            return new(target, directThreatCount, buddyThreatCount, ignoredActiveFateThreatCount, excludeActiveFateThreats, true, false, false, false, threatCountChanged);

        if (directThreatCount < ThreatClearEnterCount || target == null)
        {
            _threatClearActive = false;
            _committedThreatTargetId = 0;
            return new(null, directThreatCount, buddyThreatCount, ignoredActiveFateThreatCount, excludeActiveFateThreats, false, false, true, previousTargetId != 0, threatCountChanged);
        }

        _committedThreatTargetId = target.GameObjectId;
        return new(target, directThreatCount, buddyThreatCount, ignoredActiveFateThreatCount, excludeActiveFateThreats, true, false, false, target.GameObjectId != previousTargetId, threatCountChanged);
    }

    internal IBattleNpc? SelectFallbackDirectThreat(ushort activeFateId, Vector3 fateCenter, float fateRadius)
        => FateTargeting.GetBestDirectThreatTarget(activeFateId, 0, fateCenter, fateRadius, out _, out _, out _);

    internal void Reset()
    {
        _threatClearActive = false;
        _committedThreatTargetId = 0;
        _lastDirectThreatCount = 0;
        _lastBuddyThreatCount = 0;
        _lastIgnoredActiveFateThreatCount = 0;
    }
}
