using Dalamud.Configuration;
using Dalamud.Game.Text;
using Newtonsoft.Json;
using System.Text.Json.Serialization;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.InformationWindow;
using ZodiacBuddy.Stages.Brave;
using ZodiacBuddy.Stages.Novus;

namespace ZodiacBuddy;

public class PluginConfiguration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    [JsonProperty("BraveEchoChannel")] public XivChatType ChatType { get; set; } = XivChatType.Echo;

    public bool BraveEchoTarget { get; set; } = true;
    public bool TargetInfoWindowWasOpen { get; set; } = false;
    public bool BraveCopyTarget { get; set; } = true;

    [JsonPropertyName("IsAtmaManagerEnabled")]
    public bool IsAtmaManagerEnabled { get; set; } = false;

    /// <summary>Automatically progress to the next enemy set after all three kills are complete.</summary>
    public bool AutoAdvanceEnemy { get; set; } = false;

    /// <summary>Automatically level-sync to the FATE's level on arrival.</summary>
    public bool AutoFateLevelSync { get; set; } = true;

    public BonusLightConfiguration BonusLight { get; } = new();

    public NovusConfiguration Novus { get; } = new();

    public BraveConfiguration Brave { get; } = new();

    public InformationWindowConfiguration InformationWindow { get; } = new();

    public bool DisableTeleport = false;

    public void Save() => Service.Interface.SavePluginConfig(this);
}
