using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace ZodiacBuddy.Helpers
{
    public static class TargetingHelper
    {
        /// <summary>
        /// Attempts to target an enemy by exact name match.
        /// </summary>
        /// <param name="name">The name of the enemy to target.</param>
        /// <returns>True if the target was found and selected, false otherwise.</returns>
        public static bool TryTargetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var npc = Svc.Objects.Where(o => o.ObjectKind == ObjectKind.BattleNpc && o.Name.TextValue == name).FirstOrDefault();
            if (npc != null)
            {
                Svc.Targets.Target = npc;
                return true;
            }

            return false;
        }
    }
}