using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PmpShare.Plugin.Models;
using PmpShare.Plugin.Services;

namespace PmpShare.Plugin.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const long MaxEncryptedSizeBytes = 500L * 1024L * 1024L;
    private const int TransferIdLength = 32;

    private readonly Configuration configuration;
    private readonly PmpShareApiClient apiClient;
    private readonly TransferCrypto transferCrypto;

    private string apiBaseUrl;
    private string testerKey = string.Empty;
    private string uploadPlaintextPath;
    private string uploadPassphrase = string.Empty;
    private string uploadResult = string.Empty;
    private string downloadTransferId = string.Empty;
    private string downloadDirectory;
    private string downloadPassphrase = string.Empty;
    private string downloadResult = string.Empty;
    private string lastTransferId = string.Empty;
    private string lastMetadataUrl = string.Empty;
    private bool isBusy;
    private CancellationTokenSource? operationCts;

    public MainWindow(Configuration configuration, PmpShareApiClient apiClient, TransferCrypto transferCrypto)
        : base("PmpShare###PmpShareMainWindow")
    {
        this.configuration = configuration;
        this.apiClient = apiClient;
        this.transferCrypto = transferCrypto;
        apiBaseUrl = configuration.ApiBaseUrl;
        uploadPlaintextPath = configuration.LastUploadPath;
        downloadDirectory = string.IsNullOrWhiteSpace(configuration.LastDownloadDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : configuration.LastDownloadDirectory;

        Size = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        operationCts?.Cancel();
        operationCts?.Dispose();
    }

    public override void Draw()
    {
        DrawSettings();
        ImGui.Separator();

        if (ImGui.BeginTabBar("PmpShareTabs"))
        {
            if (ImGui.BeginTabItem("Upload"))
            {
                DrawUploadTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Download"))
            {
                DrawDownloadTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawSettings()
    {
        ImGui.TextUnformatted("Relay API");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Base URL", ref apiBaseUrl, 512))
        {
            configuration.ApiBaseUrl = apiBaseUrl.Trim();
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Tester key", ref testerKey, 512, ImGuiInputTextFlags.Password))
        {
            uploadResult = "Tester key is kept in memory only and is not saved.";
        }
    }

    private void DrawUploadTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Upload an encrypted transfer");

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Plain .pmp path", ref uploadPlaintextPath, 1024))
        {
            configuration.LastUploadPath = uploadPlaintextPath;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Passphrase", ref uploadPassphrase, 512, ImGuiInputTextFlags.Password);

        if (DrawActionButton("Encrypt and upload", CanStartOperation()) && ValidateUploadInputs())
        {
            StartOperation(async token => await EncryptAndUploadAsync(token).ConfigureAwait(false));
        }

        ImGui.SameLine();
        if (DrawActionButton("Cancel", isBusy))
        {
            operationCts?.Cancel();
        }

        ImGui.Separator();
        ImGui.TextWrapped(uploadResult);

        if (!string.IsNullOrWhiteSpace(lastTransferId))
        {
            ImGui.TextUnformatted($"Transfer ID: {lastTransferId}");
            ImGui.TextUnformatted($"Metadata URL: {lastMetadataUrl}");
            if (ImGui.Button("Copy transfer ID"))
            {
                ImGui.SetClipboardText(lastTransferId);
            }

            ImGui.SameLine();
            if (ImGui.Button("Copy metadata URL"))
            {
                ImGui.SetClipboardText(lastMetadataUrl);
            }
        }
    }

    private void DrawDownloadTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Download, decrypt, verify, then complete");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Transfer ID", ref downloadTransferId, 128);

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Output directory", ref downloadDirectory, 1024))
        {
            configuration.LastDownloadDirectory = downloadDirectory;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Passphrase", ref downloadPassphrase, 512, ImGuiInputTextFlags.Password);

        if (DrawActionButton("Download and complete", CanStartOperation()) && ValidateDownloadInputs())
        {
            StartOperation(async token => await DownloadDecryptAndCompleteAsync(token).ConfigureAwait(false));
        }

        ImGui.SameLine();
        if (DrawActionButton("Cancel##Download", isBusy))
        {
            operationCts?.Cancel();
        }

        ImGui.Separator();
        ImGui.TextWrapped(downloadResult);
    }

    private async Task EncryptAndUploadAsync(CancellationToken cancellationToken)
    {
        var sourcePath = uploadPlaintextPath.Trim('"', ' ');
        var encryptedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pmpshare.bin");

        try
        {
            SetUploadResult("Encrypting locally...");
            var encryption = await transferCrypto.EncryptFileAsync(sourcePath, encryptedPath, uploadPassphrase, cancellationToken)
                .ConfigureAwait(false);

            SetUploadResult("Creating transfer metadata...");
            var createRequest = new CreateTransferRequest(
                Path.GetFileName(sourcePath),
                encryption.PlaintextSha256,
                encryption.EncryptedSize,
                10_800);
            var transfer = await apiClient.CreateTransferAsync(apiBaseUrl, testerKey, createRequest, cancellationToken)
                .ConfigureAwait(false);

            SetUploadResult("Uploading encrypted blob...");
            await apiClient.UploadBlobAsync(apiBaseUrl, testerKey, transfer.UploadUrl, encryptedPath, cancellationToken)
                .ConfigureAwait(false);

            lastTransferId = transfer.TransferId;
            lastMetadataUrl = new Uri(new Uri(apiBaseUrl), transfer.MetadataUrl).ToString();
            SetUploadResult("Upload complete. Share the transfer ID and passphrase through a private channel.");
        }
        finally
        {
            TryDelete(encryptedPath);
        }
    }

    private async Task DownloadDecryptAndCompleteAsync(CancellationToken cancellationToken)
    {
        var transferId = downloadTransferId.Trim();
        Directory.CreateDirectory(downloadDirectory);

            SetDownloadResult("Fetching metadata...");
            var metadata = await apiClient.GetMetadataAsync(apiBaseUrl, transferId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(metadata.Status, "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Transfer is not downloadable. Current status: {metadata.Status}.");
            }

            if (metadata.EncryptedSize <= 0 || metadata.EncryptedSize > MaxEncryptedSizeBytes)
            {
                throw new InvalidOperationException("Transfer metadata reports an invalid encrypted size.");
            }

            if (!metadata.FileName.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase) || metadata.FileName.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new InvalidOperationException("Transfer metadata has an unsafe file name.");
            }

            var encryptedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pmpshare.bin");
        var outputPath = UniqueOutputPath(Path.Combine(downloadDirectory, metadata.FileName));

        try
        {
            SetDownloadResult("Downloading encrypted blob...");
            await apiClient.DownloadBlobAsync(apiBaseUrl, transferId, encryptedPath, cancellationToken).ConfigureAwait(false);

            SetDownloadResult("Decrypting locally...");
            var actualSha256 = await transferCrypto.DecryptFileAsync(encryptedPath, outputPath, downloadPassphrase, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(actualSha256, metadata.PlaintextSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(outputPath);
                throw new InvalidOperationException("SHA-256 verification failed. The decrypted file was deleted.");
            }

            SetDownloadResult("Verification passed. Completing transfer and deleting server blob...");
            await apiClient.CompleteTransferAsync(apiBaseUrl, testerKey, transferId, cancellationToken).ConfigureAwait(false);
            SetDownloadResult($"Download complete: {outputPath}");
        }
        finally
        {
            TryDelete(encryptedPath);
        }
    }

    private bool ValidateUploadInputs()
    {
        var path = uploadPlaintextPath.Trim('"', ' ');
        if (!File.Exists(path))
        {
            uploadResult = "Choose an existing .pmp file.";
            return false;
        }

        if (!path.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase))
        {
            uploadResult = "Upload source must be a .pmp file.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(testerKey))
        {
            uploadResult = "API base URL and tester key are required.";
            return false;
        }

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            uploadResult = "API base URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uploadPassphrase))
        {
            uploadResult = "Passphrase is required.";
            return false;
        }

        return true;
    }

    private bool ValidateDownloadInputs()
    {
        if (string.IsNullOrWhiteSpace(downloadTransferId))
        {
            downloadResult = "Transfer ID is required.";
            return false;
        }

        if (!IsTransferId(downloadTransferId.Trim()))
        {
            downloadResult = "Transfer ID must be a 32-character lowercase hex value.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            downloadResult = "Output directory is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(testerKey))
        {
            downloadResult = "API base URL and tester key are required.";
            return false;
        }

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            downloadResult = "API base URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(downloadPassphrase))
        {
            downloadResult = "Passphrase is required.";
            return false;
        }

        return true;
    }

    private bool CanStartOperation() => !isBusy;

    private static bool DrawActionButton(string label, bool enabled)
    {
        if (!enabled)
        {
            ImGui.BeginDisabled();
        }

        var clicked = ImGui.Button(label);

        if (!enabled)
        {
            ImGui.EndDisabled();
        }

        return enabled && clicked;
    }

    private void StartOperation(Func<CancellationToken, Task> operation)
    {
        operationCts?.Dispose();
        operationCts = new CancellationTokenSource();
        isBusy = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await operation(operationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetUploadResultIfActive("Operation cancelled.");
                SetDownloadResultIfActive("Operation cancelled.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "PmpShare operation failed.");
                SetUploadResultIfActive(ex.Message);
                SetDownloadResultIfActive(ex.Message);
            }
            finally
            {
                isBusy = false;
            }
        });
    }

    private void SetUploadResult(string message) => uploadResult = message;

    private void SetDownloadResult(string message) => downloadResult = message;

    private void SetUploadResultIfActive(string message)
    {
        if (!string.IsNullOrWhiteSpace(uploadPlaintextPath))
        {
            uploadResult = message;
        }
    }

    private void SetDownloadResultIfActive(string message)
    {
        if (!string.IsNullOrWhiteSpace(downloadTransferId))
        {
            downloadResult = message;
        }
    }

    private static string UniqueOutputPath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a unique output file name.");
    }

    private static bool IsTransferId(string transferId)
    {
        if (transferId.Length != TransferIdLength)
        {
            return false;
        }

        foreach (var c in transferId)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
