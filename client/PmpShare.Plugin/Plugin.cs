using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PmpShare.Plugin.Models;
using PmpShare.Plugin.Services;
using PmpShare.Plugin.Windows;

namespace PmpShare.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pmpshare";
    private static readonly TimeSpan ActiveInboxPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleInboxPollInterval = TimeSpan.FromHours(1);
    private const int EmptyPollsBeforeIdle = 3;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("PmpShare");
    private readonly HttpClient httpClient = new();
    private readonly PmpShareApiClient apiClient;
    private readonly TransferCrypto transferCrypto = new();
    private readonly PenumbraIpcService penumbraIpcService;
    private readonly MainWindow mainWindow;
    private readonly CancellationTokenSource inboxPollCts = new();
    private readonly HashSet<string> notifiedPendingRequestIds = [];
    private readonly SemaphoreSlim inboxPollWake = new(0, 1);
    private readonly object inboxPollWakeLock = new();
    private Task? inboxPollTask;
    private int emptyPollCount;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        apiClient = new PmpShareApiClient(httpClient);
        penumbraIpcService = new PenumbraIpcService(PluginInterface);
        mainWindow = new MainWindow(Configuration, apiClient, transferCrypto, penumbraIpcService, GetLocalCharacterName);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the PmpShare temporary encrypted transfer window."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        inboxPollTask = Task.Run(() => PollInboxLoopAsync(inboxPollCts.Token));
    }

    public void Dispose()
    {
        inboxPollCts.Cancel();
        WakeInboxPoller();
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        penumbraIpcService.Dispose();
        try
        {
            inboxPollTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            ex.Handle(inner => inner is OperationCanceledException or ObjectDisposedException);
        }
        inboxPollWake.Dispose();
        inboxPollCts.Dispose();
        httpClient.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi()
    {
        mainWindow.Toggle();
        ResetInboxPolling();
    }

    private static string GetLocalCharacterName() =>
        ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;

    private async Task PollInboxLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasPendingRequests = await PollInboxOnceAsync(cancellationToken).ConfigureAwait(false);
            if (hasPendingRequests)
            {
                ResetInboxPolling();
            }
            else
            {
                emptyPollCount++;
            }

            var delay = emptyPollCount >= EmptyPollsBeforeIdle
                ? IdleInboxPollInterval
                : ActiveInboxPollInterval;
            try
            {
                await inboxPollWake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> PollInboxOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requests = await apiClient.GetInboxAsync(
                MainWindow.ApiBaseUrl,
                MainWindow.TesterKey,
                Configuration.PmpShareId,
                cancellationToken).ConfigureAwait(false);
            await Framework.RunOnFrameworkThread(() =>
            {
                mainWindow.ApplyInboxSnapshotFromPoll(requests);
                NotifyNewPendingRequests(requests);
            }).ConfigureAwait(false);
            return requests.Any(request => string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "PmpShare inbox poll failed.");
            return false;
        }
    }

    private void ResetInboxPolling()
    {
        emptyPollCount = 0;
        WakeInboxPoller();
    }

    private void WakeInboxPoller()
    {
        lock (inboxPollWakeLock)
        {
            if (inboxPollWake.CurrentCount == 0)
            {
                inboxPollWake.Release();
            }
        }
    }

    private void NotifyNewPendingRequests(IReadOnlyList<SendRequest> requests)
    {
        var pending = requests
            .Where(request => string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pendingIds = pending.Select(request => request.RequestId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        notifiedPendingRequestIds.RemoveWhere(id => !pendingIds.Contains(id));

        foreach (var request in pending.Where(request => notifiedPendingRequestIds.Add(request.RequestId)))
        {
            var sender = string.IsNullOrWhiteSpace(request.SenderDisplayName)
                ? request.SenderId
                : request.SenderDisplayName;
            ChatGui.Print($"[PmpShare] New receive request from {sender}. Open /pmpshare > Receive to accept it.");
        }
    }
}
