using System.Diagnostics;
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
    private readonly PenumbraIpcService penumbra;

    private string apiBaseUrl;
    private string testerKey = string.Empty;
    private string uploadPlaintextPath;
    private string uploadPassphrase = string.Empty;
    private string downloadTransferId = string.Empty;
    private string downloadDirectory;
    private string downloadPassphrase = string.Empty;
    private string penumbraExportFolder;
    private string contactName = string.Empty;
    private string contactId = string.Empty;
    private string selectedModDirectory = string.Empty;
    private string selectedModName = string.Empty;
    private string selectedModPath = string.Empty;
    private string foundExportedPmp = string.Empty;
    private string lastReceivedPmp = string.Empty;
    private string statusText = string.Empty;
    private string uploadResult = string.Empty;
    private string receiveResult = string.Empty;
    private string contactResult = string.Empty;
    private string penumbraResult = string.Empty;
    private string lastTransferId = string.Empty;
    private string lastMetadataUrl = string.Empty;
    private bool sendFromPenumbraMod;
    private bool verifiedReceiveReady;
    private bool isBusy;
    private int selectedContactIndex;
    private List<SendRequest> inbox = [];
    private CancellationTokenSource? operationCts;

    public MainWindow(
        Configuration configuration,
        PmpShareApiClient apiClient,
        TransferCrypto transferCrypto,
        PenumbraIpcService penumbra)
        : base("PmpShare###PmpShareMainWindow")
    {
        this.configuration = configuration;
        this.apiClient = apiClient;
        this.transferCrypto = transferCrypto;
        this.penumbra = penumbra;
        apiBaseUrl = configuration.ApiBaseUrl;
        uploadPlaintextPath = configuration.LastUploadPath;
        downloadDirectory = string.IsNullOrWhiteSpace(configuration.LastDownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PmpShare")
            : configuration.LastDownloadDirectory;
        penumbraExportFolder = configuration.PenumbraExportFolder;
        Size = new Vector2(860, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        operationCts?.Cancel();
        operationCts?.Dispose();
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("PmpShareTabs"))
        {
            DrawTab("Status", DrawStatusTab);
            DrawTab("Send", DrawSendTab);
            DrawTab("Receive", DrawReceiveTab);
            DrawTab("Contacts", DrawContactsTab);
            DrawTab("Penumbra", DrawPenumbraTab);
            DrawTab("Settings", DrawSettingsTab);
            ImGui.EndTabBar();
        }
    }

    private static void DrawTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label))
        {
            return;
        }
        draw();
        ImGui.EndTabItem();
    }

    private void DrawStatusTab()
    {
        var penumbraStatus = penumbra.CurrentStatus;
        ImGui.TextUnformatted($"My PmpShare ID: {configuration.PmpShareId}");
        if (ImGui.Button("Copy My PmpShare ID"))
        {
            ImGui.SetClipboardText(configuration.PmpShareId);
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Worker URL: {apiBaseUrl}");
        ImGui.TextUnformatted($"Penumbra available: {penumbraStatus.IsAvailable}");
        ImGui.TextUnformatted($"Penumbra API version: {penumbraStatus.ApiVersion}");
        ImGui.TextUnformatted($"Penumbra enabled: {penumbraStatus.IsEnabled}");
        ImGui.TextWrapped($"Penumbra mod root: {penumbraStatus.ModRoot}");
        if (!string.IsNullOrWhiteSpace(penumbraStatus.LastError))
        {
            ImGui.TextWrapped($"Penumbra error: {penumbraStatus.LastError}");
        }

        ImGui.Separator();
        ImGui.TextWrapped(statusText);
    }

    private void DrawSendTab()
    {
        ImGui.Checkbox("Send from installed Penumbra mod", ref sendFromPenumbraMod);
        if (sendFromPenumbraMod)
        {
            DrawPenumbraModSelector();
            ImGui.InputText("Export folder", ref penumbraExportFolder, 1024);
            if (ImGui.Button("Find Exported PMP"))
            {
                foundExportedPmp = FindExportedPmp(selectedModName, penumbraExportFolder) ?? string.Empty;
                uploadPlaintextPath = foundExportedPmp;
                uploadResult = string.IsNullOrEmpty(foundExportedPmp) ? "No matching exported .pmp found." : $"Found {foundExportedPmp}";
            }

            ImGui.SameLine();
            if (ImGui.Button("Open Penumbra Mod Folder"))
            {
                OpenFolder(selectedModPath);
            }

            ImGui.SameLine();
            if (ImGui.Button("Open Export Folder"))
            {
                OpenFolder(penumbraExportFolder);
            }
            ImGui.TextWrapped($"Selected mod path: {selectedModPath}");
            ImGui.TextWrapped($"Found exported PMP: {foundExportedPmp}");
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Existing .pmp file", ref uploadPlaintextPath, 1024))
        {
            configuration.LastUploadPath = uploadPlaintextPath;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Passphrase", ref uploadPassphrase, 512, ImGuiInputTextFlags.Password);
        var selectedContact = DrawContactSelector();
        if (selectedContact is not null)
        {
            ImGui.TextUnformatted($"Selected contact: {selectedContact.DisplayName} ({selectedContact.PmpShareId})");
        }

        if (DrawActionButton("Upload / Share Code", CanStartOperation()) && ValidateUploadInputs())
        {
            StartOperation(async token => await EncryptAndUploadAsync(token).ConfigureAwait(false));
        }

        ImGui.SameLine();
        if (DrawActionButton("Create Send Request", CanStartOperation()) && ValidateSendRequestInputs())
        {
            StartOperation(async token => await CreateSendRequestAsync(token).ConfigureAwait(false));
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
        }
    }

    private void DrawReceiveTab()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Transfer ID", ref downloadTransferId, 128);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Staging/output directory", ref downloadDirectory, 1024))
        {
            configuration.LastDownloadDirectory = downloadDirectory;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Passphrase", ref downloadPassphrase, 512, ImGuiInputTextFlags.Password);
        if (DrawActionButton("Download / Decrypt / Verify", CanStartOperation()) && ValidateDownloadInputs())
        {
            StartOperation(async token => await DownloadDecryptAndVerifyAsync(token).ConfigureAwait(false));
        }

        ImGui.SameLine();
        if (DrawActionButton("Refresh Inbox", CanStartOperation()) && HasApiAuth())
        {
            StartOperation(async token => await RefreshInboxAsync(token).ConfigureAwait(false));
        }

        ImGui.Separator();
        ImGui.TextWrapped(receiveResult);
        if (verifiedReceiveReady)
        {
            ImGui.TextUnformatted($"Verified PMP: {lastReceivedPmp}");
            if (ImGui.Button("Import to Penumbra"))
            {
                ImportLastReceivedToPenumbra();
            }

            ImGui.SameLine();
            if (ImGui.Button("Save only"))
            {
                receiveResult = $"Verified and saved only: {lastReceivedPmp}";
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Inbox requests");
        foreach (var request in inbox)
        {
            ImGui.TextWrapped($"{request.SenderDisplayName ?? request.SenderId}: {request.Status} {request.Message}");
            if (request.Status == "pending" && ImGui.Button($"Accept##{request.RequestId}"))
            {
                StartOperation(async token => await AcceptRequestAsync(request, token).ConfigureAwait(false));
            }
            ImGui.SameLine();
            if (request.Status == "pending" && ImGui.Button($"Decline##{request.RequestId}"))
            {
                StartOperation(async token => await DeclineRequestAsync(request, token).ConfigureAwait(false));
            }
        }
    }

    private void DrawContactsTab()
    {
        ImGui.InputText("Display name", ref contactName, 128);
        ImGui.InputText("PmpShare ID", ref contactId, 128);
        if (ImGui.Button("Add contact"))
        {
            if (string.IsNullOrWhiteSpace(contactName) || string.IsNullOrWhiteSpace(contactId))
            {
                contactResult = "Display name and PmpShare ID are required.";
            }
            else
            {
                configuration.Contacts.Add(new Contact { DisplayName = contactName.Trim(), PmpShareId = contactId.Trim() });
                configuration.Save();
                contactName = string.Empty;
                contactId = string.Empty;
                contactResult = "Contact added.";
            }
        }

        ImGui.TextWrapped(contactResult);
        ImGui.Separator();
        for (var i = 0; i < configuration.Contacts.Count; i++)
        {
            var contact = configuration.Contacts[i];
            ImGui.TextUnformatted($"{contact.DisplayName} - {contact.PmpShareId}");
            ImGui.SameLine();
            if (ImGui.Button($"Remove##contact{i}"))
            {
                configuration.Contacts.RemoveAt(i);
                configuration.Save();
                break;
            }
        }
    }

    private void DrawPenumbraTab()
    {
        if (ImGui.Button("Refresh Penumbra Status"))
        {
            penumbra.RefreshStatus();
        }

        var status = penumbra.CurrentStatus;
        ImGui.TextUnformatted($"Available: {status.IsAvailable}");
        ImGui.TextUnformatted($"API version: {status.ApiVersion}");
        ImGui.TextUnformatted($"Enabled: {status.IsEnabled}");
        ImGui.TextWrapped($"Mod root: {status.ModRoot}");
        ImGui.TextWrapped($"Last error: {status.LastError}");
        DrawPenumbraModSelector();
        ImGui.TextWrapped($"Selected mod path: {selectedModPath}");
        ImGui.TextWrapped(penumbraResult);
    }

    private void DrawSettingsTab()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("API base URL", ref apiBaseUrl, 512))
        {
            configuration.ApiBaseUrl = apiBaseUrl.Trim();
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Tester key (memory only)", ref testerKey, 512, ImGuiInputTextFlags.Password);

        var autoImport = configuration.AutoImportToPenumbraAfterReceive;
        if (ImGui.Checkbox("Auto import to Penumbra after receive", ref autoImport))
        {
            configuration.AutoImportToPenumbraAfterReceive = autoImport;
            configuration.Save();
        }
        var requiresConfirmation = configuration.PenumbraImportRequiresConfirmation;
        if (ImGui.Checkbox("Penumbra import requires confirmation", ref requiresConfirmation))
        {
            configuration.PenumbraImportRequiresConfirmation = requiresConfirmation;
            configuration.Save();
        }
        var deleteAfterImport = configuration.DeleteAfterSuccessfulPenumbraImport;
        if (ImGui.Checkbox("Delete after successful Penumbra import", ref deleteAfterImport))
        {
            configuration.DeleteAfterSuccessfulPenumbraImport = deleteAfterImport;
            configuration.Save();
        }
        var keepIfImportFails = configuration.KeepDownloadedPmpIfImportFails;
        if (ImGui.Checkbox("Keep downloaded PMP if import fails", ref keepIfImportFails))
        {
            configuration.KeepDownloadedPmpIfImportFails = keepIfImportFails;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Penumbra export folder", ref penumbraExportFolder, 1024))
        {
            configuration.PenumbraExportFolder = penumbraExportFolder;
            configuration.Save();
        }
    }

    private void DrawPenumbraModSelector()
    {
        if (ImGui.Button("Refresh installed mods"))
        {
            penumbra.RefreshStatus();
        }

        foreach (var (directory, name) in penumbra.CurrentStatus.InstalledMods.OrderBy(kvp => kvp.Value))
        {
            if (ImGui.Selectable($"{name} ({directory})", selectedModDirectory == directory))
            {
                selectedModDirectory = directory;
                selectedModName = name;
                try
                {
                    var path = penumbra.GetModPath(directory, name);
                    selectedModPath = path.Path;
                    penumbraResult = $"GetModPath result: {path.ResultCode}";
                }
                catch (Exception ex)
                {
                    selectedModPath = string.Empty;
                    penumbraResult = ex.Message;
                }
            }
        }
    }

    private Contact? DrawContactSelector()
    {
        if (configuration.Contacts.Count == 0)
        {
            ImGui.TextUnformatted("No contacts yet.");
            return null;
        }

        selectedContactIndex = Math.Clamp(selectedContactIndex, 0, configuration.Contacts.Count - 1);
        var preview = configuration.Contacts[selectedContactIndex].DisplayName;
        if (ImGui.BeginCombo("Contact", preview))
        {
            for (var i = 0; i < configuration.Contacts.Count; i++)
            {
                var contact = configuration.Contacts[i];
                if (ImGui.Selectable($"{contact.DisplayName} ({contact.PmpShareId})", selectedContactIndex == i))
                {
                    selectedContactIndex = i;
                }
            }

            ImGui.EndCombo();
        }

        return configuration.Contacts[selectedContactIndex];
    }

    private async Task EncryptAndUploadAsync(CancellationToken cancellationToken)
    {
        var sourcePath = uploadPlaintextPath.Trim('"', ' ');
        var encryptedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pmpshare.bin");
        try
        {
            uploadResult = "Encrypting locally...";
            var encryption = await transferCrypto.EncryptFileAsync(sourcePath, encryptedPath, uploadPassphrase, cancellationToken).ConfigureAwait(false);
            var transfer = await apiClient.CreateTransferAsync(apiBaseUrl, testerKey, new CreateTransferRequest(Path.GetFileName(sourcePath), encryption.PlaintextSha256, encryption.EncryptedSize, 10_800), cancellationToken).ConfigureAwait(false);
            uploadResult = "Uploading encrypted blob...";
            await apiClient.UploadBlobAsync(apiBaseUrl, testerKey, transfer.UploadUrl, encryptedPath, cancellationToken).ConfigureAwait(false);
            lastTransferId = transfer.TransferId;
            lastMetadataUrl = new Uri(new Uri(apiBaseUrl), transfer.MetadataUrl).ToString();
            uploadResult = "Upload complete. Share the transfer ID and passphrase privately.";
        }
        finally
        {
            TryDelete(encryptedPath);
        }
    }

    private async Task DownloadDecryptAndVerifyAsync(CancellationToken cancellationToken)
    {
        var transferId = downloadTransferId.Trim();
        Directory.CreateDirectory(downloadDirectory);
        receiveResult = "Fetching metadata...";
        var metadata = await apiClient.GetMetadataAsync(apiBaseUrl, transferId, cancellationToken).ConfigureAwait(false);
        ValidateMetadata(metadata);

        var encryptedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pmpshare.bin");
        var finalPath = UniqueOutputPath(Path.Combine(downloadDirectory, metadata.FileName));
        var partPath = finalPath + ".part";
        try
        {
            receiveResult = "Downloading encrypted blob...";
            await apiClient.DownloadBlobAsync(apiBaseUrl, transferId, encryptedPath, cancellationToken).ConfigureAwait(false);
            receiveResult = "Decrypting locally to staging...";
            var actualSha256 = await transferCrypto.DecryptFileAsync(encryptedPath, partPath, downloadPassphrase, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha256, metadata.PlaintextSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partPath);
                throw new InvalidOperationException("SHA-256 verification failed. The decrypted staging file was deleted.");
            }

            if (File.Exists(finalPath))
            {
                finalPath = UniqueOutputPath(finalPath);
            }
            File.Move(partPath, finalPath);
            await apiClient.CompleteTransferAsync(apiBaseUrl, testerKey, transferId, cancellationToken).ConfigureAwait(false);
            lastReceivedPmp = finalPath;
            verifiedReceiveReady = true;
            receiveResult = $"Verified and saved: {finalPath}";
            if (configuration.AutoImportToPenumbraAfterReceive && !configuration.PenumbraImportRequiresConfirmation)
            {
                ImportLastReceivedToPenumbra();
            }
            else if (!penumbra.CurrentStatus.IsAvailable)
            {
                receiveResult += "\nPenumbra unavailable; file saved only.";
            }
        }
        finally
        {
            TryDelete(encryptedPath);
        }
    }

    private async Task CreateSendRequestAsync(CancellationToken cancellationToken)
    {
        if (configuration.Contacts.Count == 0)
        {
            uploadResult = "Add a contact first.";
            return;
        }

        selectedContactIndex = Math.Clamp(selectedContactIndex, 0, configuration.Contacts.Count - 1);
        var contact = configuration.Contacts[selectedContactIndex];

        var request = await apiClient.CreateSendRequestAsync(
            apiBaseUrl,
            testerKey,
            new CreateSendRequestRequest(configuration.PmpShareId, contact.PmpShareId, "PmpShare tester", lastTransferId, "Receiver accepted means copy/share the transfer ID and passphrase manually.", 10_800),
            cancellationToken).ConfigureAwait(false);
        uploadResult = $"Send request created: {request.RequestId}. Receiver must accept; share code/passphrase still stays manual for this MVP.";
    }

    private async Task RefreshInboxAsync(CancellationToken cancellationToken)
    {
        inbox = (await apiClient.GetInboxAsync(apiBaseUrl, testerKey, configuration.PmpShareId, cancellationToken).ConfigureAwait(false)).ToList();
        receiveResult = $"Loaded {inbox.Count} inbox request(s).";
    }

    private async Task AcceptRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        await apiClient.AcceptSendRequestAsync(apiBaseUrl, testerKey, request.RequestId, cancellationToken).ConfigureAwait(false);
        receiveResult = "Request accepted. Sender should now share/copy the transfer ID and passphrase manually.";
        await RefreshInboxAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeclineRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        await apiClient.DeclineSendRequestAsync(apiBaseUrl, testerKey, request.RequestId, cancellationToken).ConfigureAwait(false);
        await RefreshInboxAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ImportLastReceivedToPenumbra()
    {
        if (string.IsNullOrWhiteSpace(lastReceivedPmp) || !File.Exists(lastReceivedPmp))
        {
            receiveResult = "No verified .pmp is ready to import.";
            return;
        }
        if (!penumbra.RefreshStatus().IsAvailable)
        {
            receiveResult = "Penumbra unavailable; file saved only.";
            return;
        }

        try
        {
            var result = penumbra.InstallMod(lastReceivedPmp);
            receiveResult = $"Penumbra InstallMod result code: {result.ResultCode}. Success means queued for install, not guaranteed fully installed.";
            if (result.QueuedForInstall && configuration.DeleteAfterSuccessfulPenumbraImport)
            {
                TryDelete(lastReceivedPmp);
                verifiedReceiveReady = false;
                receiveResult += "\nDeleted local decrypted .pmp after successful Penumbra queue.";
            }
        }
        catch (Exception ex)
        {
            receiveResult = $"Penumbra import failed; .pmp kept in staging. {ex.Message}";
        }
    }

    private bool ValidateUploadInputs()
    {
        var path = uploadPlaintextPath.Trim('"', ' ');
        if (!File.Exists(path) || !path.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase))
        {
            uploadResult = "Choose an existing .pmp file.";
            return false;
        }
        if (!HasApiAuth() || string.IsNullOrWhiteSpace(uploadPassphrase))
        {
            uploadResult = "API base URL, tester key, and passphrase are required.";
            return false;
        }
        return true;
    }

    private bool ValidateSendRequestInputs()
    {
        if (!HasApiAuth())
        {
            uploadResult = "API base URL and tester key are required.";
            return false;
        }
        if (configuration.Contacts.Count == 0)
        {
            uploadResult = "Add a contact first.";
            return false;
        }
        return true;
    }

    private bool ValidateDownloadInputs()
    {
        if (!IsTransferId(downloadTransferId.Trim()))
        {
            receiveResult = "Transfer ID must be a 32-character lowercase hex value.";
            return false;
        }
        if (!HasApiAuth() || string.IsNullOrWhiteSpace(downloadDirectory) || string.IsNullOrWhiteSpace(downloadPassphrase))
        {
            receiveResult = "API base URL, tester key, output directory, and passphrase are required.";
            return false;
        }
        return true;
    }

    private bool HasApiAuth() =>
        Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrWhiteSpace(testerKey);

    private static void ValidateMetadata(TransferMetadata metadata)
    {
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
                statusText = "Operation cancelled.";
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "PmpShare operation failed.");
                statusText = ex.Message;
                uploadResult = ex.Message;
                receiveResult = ex.Message;
            }
            finally
            {
                isBusy = false;
            }
        });
    }

    private static string? FindExportedPmp(string modName, string exportFolder)
    {
        if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(exportFolder) || !Directory.Exists(exportFolder))
        {
            return null;
        }
        var normalized = NormalizeName(modName);
        return Directory.EnumerateFiles(exportFolder, "*.pmp", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => NormalizeName(Path.GetFileNameWithoutExtension(file.Name)).Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
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
        return transferId.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
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
