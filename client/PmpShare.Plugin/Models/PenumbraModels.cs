namespace PmpShare.Plugin.Models;

public sealed class PenumbraStatus
{
    public bool IsAvailable { get; set; }

    public int ApiVersion { get; set; }

    public bool IsEnabled { get; set; }

    public string ModRoot { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public Dictionary<string, string> InstalledMods { get; set; } = [];
}

public sealed record PenumbraModPathResult(int ResultCode, string Path, bool FullPathDefault, bool SortOrderDefault);

public sealed record PenumbraInstallResult(int ResultCode, bool QueuedForInstall);
