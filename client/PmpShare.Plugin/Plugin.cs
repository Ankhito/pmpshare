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

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("PmpShare");
    private readonly HttpClient httpClient = new();
    private readonly PmpShareApiClient apiClient;
    private readonly TransferCrypto transferCrypto = new();
    private readonly PenumbraIpcService penumbraIpcService;
    private readonly MainWindow mainWindow;
    private readonly CancellationTokenSource inboxPollCts = new();
    private readonly HashSet<string> notifiedPendingRequestIds = [];
    private Task? inboxPollTask;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        apiClient = new PmpShareApiClient(httpClient);
        penumbraIpcService = new PenumbraIpcService(PluginInterface);
        mainWindow = new MainWindow(Configuration, apiClient, transferCrypto, penumbraIpcService);
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
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        penumbraIpcService.Dispose();
        inboxPollCts.Dispose();
        httpClient.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi() => mainWindow.Toggle();

    private async Task PollInboxLoopAsync(CancellationToken cancellationToken)
    {
        await PollInboxOnceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await PollInboxOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollInboxOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requests = await apiClient.GetInboxAsync(
                MainWindow.ApiBaseUrl,
                MainWindow.TesterKey,
                Configuration.PmpShareId,
                cancellationToken).ConfigureAwait(false);
            mainWindow.ApplyInboxSnapshotFromPoll(requests);
            NotifyNewPendingRequests(requests);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "PmpShare inbox poll failed.");
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
