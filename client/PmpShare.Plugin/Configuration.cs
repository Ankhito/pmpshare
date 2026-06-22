using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PmpShare.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;

    public string ApiBaseUrl { get; set; } = "https://pmpshare-api.contact-theankh.workers.dev";

    public string LastUploadPath { get; set; } = string.Empty;

    public string LastDownloadDirectory { get; set; } = string.Empty;

    public void Initialize(IDalamudPluginInterface dalamudPluginInterface)
    {
        pluginInterface = dalamudPluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
