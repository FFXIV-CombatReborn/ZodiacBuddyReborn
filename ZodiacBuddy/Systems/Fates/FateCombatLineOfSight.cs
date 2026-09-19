using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System.Numerics;

namespace ZodiacBuddy.Systems.Fates;

internal static class FateCombatLineOfSight
{
    private const float EyeHeight = 2f;

    internal static bool HasLineOfSight(Vector3 sourcePosition, Vector3 targetPosition)
    {
        sourcePosition.Y += EyeHeight;
        targetPosition.Y += EyeHeight;

        var offset = targetPosition - sourcePosition;
        var distance = offset.Length();
        if (distance <= 0.001f)
            return true;

        var direction = offset / distance;
        return !BGCollisionModule.RaycastMaterialFilter(sourcePosition, direction, out _, distance);
    }
}
