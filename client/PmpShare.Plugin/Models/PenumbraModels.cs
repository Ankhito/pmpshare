namespace PmpShare.Plugin.Models;

public sealed class PenumbraStatus
{
    public bool IsAvailable { get; set; }

    public string ApiVersion { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public string ModRoot { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public Dictionary<string, string> InstalledMods { get; set; } = [];
}

public sealed record PenumbraModPathResult(int ResultCode, string Path, bool FullPathDefault, bool SortOrderDefault);

public sealed record PenumbraInstallResult(int ResultCode, bool QueuedForInstall);
