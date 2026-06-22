using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using PmpShare.Plugin.Models;

namespace PmpShare.Plugin.Services;

public sealed class PenumbraIpcService : IDisposable
{
    private readonly ICallGateSubscriber<(int Breaking, int Feature)> apiVersion;
    private readonly ICallGateSubscriber<bool> getEnabledState;
    private readonly ICallGateSubscriber<string> getModDirectory;
    private readonly ICallGateSubscriber<Dictionary<string, string>> getModList;
    private readonly ICallGateSubscriber<string, int> installMod;
    private readonly ICallGateSubscriber<string, string, (int Result, string Path, bool FullPathDefault, bool SortOrderDefault)> getModPath;
    private readonly ICallGateSubscriber<object> initialized;
    private readonly ICallGateSubscriber<object> disposed;

    private readonly Action penumbraChanged;
    private PenumbraStatus status = new();

    public PenumbraIpcService(IDalamudPluginInterface pluginInterface)
    {
        apiVersion = pluginInterface.GetIpcSubscriber<(int Breaking, int Feature)>("Penumbra.ApiVersion.V5");
        getEnabledState = pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
        getModDirectory = pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        getModList = pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        installMod = pluginInterface.GetIpcSubscriber<string, int>("Penumbra.InstallMod.V5");
        getModPath = pluginInterface.GetIpcSubscriber<string, string, (int, string, bool, bool)>("Penumbra.GetModPath.V5");
        initialized = pluginInterface.GetIpcSubscriber<object>("Penumbra.Initialized");
        disposed = pluginInterface.GetIpcSubscriber<object>("Penumbra.Disposed");

        penumbraChanged = () => RefreshStatus();
        TrySubscribe(initialized);
        TrySubscribe(disposed);
        RefreshStatus();
    }

    public PenumbraStatus CurrentStatus => status;

    public PenumbraStatus RefreshStatus()
    {
        var next = new PenumbraStatus();
        try
        {
            var version = apiVersion.InvokeFunc();
            next.ApiVersion = $"{version.Breaking}.{version.Feature}";
            next.IsEnabled = getEnabledState.InvokeFunc();
            next.ModRoot = getModDirectory.InvokeFunc() ?? string.Empty;
            next.InstalledMods = getModList.InvokeFunc() ?? [];
            next.IsAvailable = true;
        }
        catch (Exception ex)
        {
            next.IsAvailable = false;
            next.LastError = ex.Message;
        }

        status = next;
        return status;
    }

    public IReadOnlyDictionary<string, string> GetInstalledMods()
    {
        RefreshStatus();
        return status.InstalledMods;
    }

    public PenumbraModPathResult GetModPath(string modDirectory, string modName)
    {
        var result = getModPath.InvokeFunc(modDirectory, modName);
        return new PenumbraModPathResult(result.Result, result.Path, result.FullPathDefault, result.SortOrderDefault);
    }

    public PenumbraInstallResult InstallMod(string pmpPath)
    {
        var result = installMod.InvokeFunc(pmpPath);
        return new PenumbraInstallResult(result, result == 0);
    }

    public void Dispose()
    {
        TryUnsubscribe(initialized);
        TryUnsubscribe(disposed);
    }

    private void TrySubscribe(ICallGateSubscriber<object> subscriber)
    {
        try
        {
            subscriber.Subscribe(penumbraChanged);
        }
        catch (Exception ex)
        {
            status.LastError = ex.Message;
        }
    }

    private void TryUnsubscribe(ICallGateSubscriber<object> subscriber)
    {
        try
        {
            subscriber.Unsubscribe(penumbraChanged);
        }
        catch
        {
        }
    }
}
