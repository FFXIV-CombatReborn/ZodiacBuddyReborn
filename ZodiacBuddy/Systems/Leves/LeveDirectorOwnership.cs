using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace ZodiacBuddy.Systems.Leves;

internal static unsafe class LeveDirectorOwnership
{
    internal static Director* GetActive(uint leveId)
    {
        if (leveId == 0)
            return null;

        var uiState = UIState.Instance();
        if (uiState != null)
        {
            var director = uiState->DirectorTodo.Director;
            if (Matches(director, leveId))
                return director;
        }

        var framework = EventFramework.Instance();
        if (framework == null)
            return null;

        foreach (Director* director in framework->DirectorModule.DirectorList)
        {
            if (Matches(director, leveId))
                return director;
        }

        return null;
    }

    internal static bool Matches(Director* director, uint leveId)
    {
        if (director == null || director->ContentId != leveId)
            return false;

        var content = director->Info.EventId.ContentId;
        return content is EventHandlerContent.BattleLeveDirector or EventHandlerContent.CompanyLeveDirector;
    }

    internal static bool Belongs(IGameObject obj, Director* director)
    {
        if (obj.Address == nint.Zero || director == null)
            return false;

        var gameObject = (GameObject*)obj.Address;
        var eventHandler = (EventHandler*)director;
        return gameObject->EventHandler == eventHandler || gameObject->EventId == director->Info.EventId;
    }
}
