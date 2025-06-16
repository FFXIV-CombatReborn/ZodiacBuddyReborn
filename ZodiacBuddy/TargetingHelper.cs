using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System;

namespace ZodiacBuddy.Helpers;

public static unsafe class TargetingHelper
{
    private static ulong _storedTargetId;

    public static ulong StoredTargetId
    {
        get => _storedTargetId;
        set => _storedTargetId = value;
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

}
