using Dalamud.Configuration;
using Dalamud.Game.Text;
using Newtonsoft.Json;
using System.Text.Json.Serialization;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.InformationWindow;
using ZodiacBuddy.Stages.Atma;
using ZodiacBuddy.Stages.Brave;
using ZodiacBuddy.Stages.Novus;
using ZodiacBuddy.Stages.Zenith;

namespace ZodiacBuddy;

public class PluginConfiguration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    [JsonProperty("BraveEchoChannel")] public XivChatType ChatType { get; set; } = XivChatType.Echo;

    public bool BraveEchoTarget { get; set; } = true;

    public bool BraveCopyTarget { get; set; } = true;

    [JsonPropertyName("IsAtmaManagerEnabled")]
    public bool IsAtmaManagerEnabled { get; set; } = false;
    public BonusLightConfiguration BonusLight { get; } = new();

    public NovusConfiguration Novus { get; } = new();

    public BraveConfiguration Brave { get; } = new();

    public ZenithConfiguration Zenith { get; } = new();

    public InformationWindowConfiguration InformationWindow { get; } = new();

    public bool DisableTeleport = false;

    public void Save() => Service.Interface.SavePluginConfig(this);
}