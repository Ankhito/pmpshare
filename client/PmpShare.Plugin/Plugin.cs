using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("PmpShare");
    private readonly HttpClient httpClient = new();
    private readonly PmpShareApiClient apiClient;
    private readonly TransferCrypto transferCrypto = new();
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        apiClient = new PmpShareApiClient(httpClient);
        mainWindow = new MainWindow(Configuration, apiClient, transferCrypto);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the PmpShare temporary encrypted transfer window."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        httpClient.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi() => mainWindow.Toggle();
}
