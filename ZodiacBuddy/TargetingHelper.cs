using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;
using System;
using System.Collections.Generic;
using static FFXIVClientStructs.FFXIV.Client.Game.UI.MobHunt;

namespace ZodiacBuddy.Helpers;

public static unsafe class TargetingHelper
{
    private static ulong _storedTargetId;
    private static int _killCount = 0;
    private static ulong _lastKilledId = 0;
    private static string? _killTrackingTarget = null;
    private static readonly HashSet<ulong> _registeredKills = new();
    public static int KillCount => _killCount;
    public static void RegisterKillIfMatches(ulong killedId, string currentTargetName)
    {
        if (_killTrackingTarget == null || _killCount >= 3)
            return;

        if (_killTrackingTarget.Equals(currentTargetName, StringComparison.OrdinalIgnoreCase)
            && killedId != 0 && !_registeredKills.Contains(killedId))
        {
            _registeredKills.Add(killedId);
            _killCount++;
            _lastKilledId = killedId;
            Service.PluginLog.Debug($"Registered kill for enemy '{currentTargetName}'. Total: {_killCount}");
        }
    }
    public static void StartKillTracking(string targetName)
    {
        _killTrackingTarget = targetName;
        _killCount = 0;
        _lastKilledId = 0;
        _registeredKills.Clear();
        _hasAutoTargeted = false;
    }

    public static ulong StoredTargetId
    {
        get => _storedTargetId;
        set => _storedTargetId = value;
    }
    private static bool _hasAutoTargeted = false;

    public static void ResetAutoTargetFlag()
    {
        _hasAutoTargeted = false;
    }
    public static unsafe bool TryTargetById(ulong gameObjectId)
    {
        if (gameObjectId == 0) return false;

        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleChara battleChara && battleChara.GameObjectId == gameObjectId)
            {
                TargetSystem.Instance()->Target = (GameObject*)battleChara.Address;
                return true;
            }
        }

        return false;
    }

    public static unsafe bool TryTargetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleChara battleChara && battleChara.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                TargetSystem.Instance()->SoftTarget = (GameObject*)battleChara.Address;
                return true;
            }
        }

        return false;
    }
    public static unsafe void AutoTargetStoredIdIfVisible()
    {
        if (_storedTargetId == 0 || _hasAutoTargeted || _killCount >= 3 || Svc.ClientState.LocalPlayer == null)
            return;


        var playerPos = Svc.ClientState.LocalPlayer.Position;
        var currentTarget = Svc.Targets.Target;

        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleChara battleChara && battleChara.GameObjectId == _storedTargetId)
            {
                if (currentTarget != null && currentTarget.GameObjectId == obj.GameObjectId)
                {
                    _hasAutoTargeted = true; // Already targeting the right one
                    return;
                }

                float distance = Vector3.Distance(playerPos, obj.Position);
                if (distance > 40f)
                {
                    return;
                }

                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                if (native != null && native->GetIsTargetable())
                {
                    TargetSystem.Instance()->Target = (GameObject*)battleChara.Address;
                    _hasAutoTargeted = true;
                    Service.PluginLog.Debug($"Auto-targeted enemy: {battleChara.Name.TextValue} at {distance:F1}y");
                }

                return;
            }
        }
    }
    public static unsafe void PromoteAggroingEnemy()
    {
        var player = Svc.ClientState.LocalPlayer;
        if (player == null)
            return;

        var playerId = player.GameObjectId;

        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleChara battleChara
                && battleChara.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                && battleChara.CurrentHp > 0
                && battleChara.TargetObjectId == playerId)
            {
                TargetSystem.Instance()->Target = (GameObject*)battleChara.Address;
                Service.PluginLog.Debug($"Aggroing enemy '{battleChara.Name.TextValue}' promoted to hard target.");
                return;
            }
        }
    }
}
