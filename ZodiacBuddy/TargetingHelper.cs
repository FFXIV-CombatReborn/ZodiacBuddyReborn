using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace ZodiacBuddy;

public static unsafe class TargetingHelper
{
    private static ulong _storedTargetId;
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

    public static unsafe void AutoTargetStoredIdIfVisible()
    {
        if (_storedTargetId == 0 || Player.Object == null)
            return;


        var playerPos = Player.Object.Position;
        var currentTarget = Svc.Targets.Target;
        if (_hasAutoTargeted && (currentTarget == null || currentTarget.GameObjectId != _storedTargetId))
            _hasAutoTargeted = false;

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

                var native = (GameObject*)obj.Address;
                if (native != null && native->GetIsTargetable())
                {
                    TargetSystem.Instance()->Target = (GameObject*)battleChara.Address;
                    _hasAutoTargeted = true;
                    Service.PluginLog.Verbose($"[ZodiacBuddy/TARGET] Auto-targeted enemy: {battleChara.Name.TextValue} at {distance:F1}y");
                }

                return;
            }
        }
    }
    public static unsafe bool PromoteAggroingEnemy()
    {
        var player = Player.Object;
        if (player == null)
            return false;

        var playerId = player.GameObjectId;
        var currentTargetId = Svc.Targets.Target?.GameObjectId ?? 0;

        foreach (var obj in Svc.Objects)
        {
            if (obj is IBattleChara battleChara
                && battleChara.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                && battleChara.CurrentHp > 0
                && battleChara.TargetObjectId == playerId)
            {
                var native = (GameObject*)battleChara.Address;
                if (native == null || !native->GetIsTargetable())
                    continue;

                if (currentTargetId != battleChara.GameObjectId)
                {
                    TargetSystem.Instance()->Target = native;
                    Service.PluginLog.Verbose($"[ZodiacBuddy/TARGET] Aggroing enemy '{battleChara.Name.TextValue}' promoted to hard target.");
                }
                return true;
            }
        }

        return false;
    }
}
