using Dalamud.Configuration;
using Dalamud.Plugin;
using PmpShare.Plugin.Services;

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

    public string PmpShareId { get; set; } = string.Empty;

    public string X25519PublicKeyBase64 { get; set; } = string.Empty;

    public string X25519PrivateKeyBase64 { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PenumbraExportFolder { get; set; } = string.Empty;

    public bool AutoImportToPenumbraAfterReceive { get; set; }

    public bool PenumbraImportRequiresConfirmation { get; set; } = true;

    public bool DeleteAfterSuccessfulPenumbraImport { get; set; }

    public bool KeepDownloadedPmpIfImportFails { get; set; } = true;

    public bool DeletePenumbraExportAfterUpload { get; set; } = true;

    public List<Contact> Contacts { get; set; } = [];

    public void Initialize(IDalamudPluginInterface dalamudPluginInterface)
    {
        pluginInterface = dalamudPluginInterface;
        if (IdentityService.EnsureIdentity(this))
        {
            Save();
        }
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}

[Serializable]
public sealed class Contact
{
    public string DisplayName { get; set; } = string.Empty;

    public string PmpShareId { get; set; } = string.Empty;

    public string PublicKeyBase64 { get; set; } = string.Empty;
}
