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
    private string contactIdentity = string.Empty;
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
        ImGui.TextWrapped($"My public key: {configuration.X25519PublicKeyBase64}");
        if (ImGui.Button("Copy My PmpShare Identity"))
        {
            ImGui.SetClipboardText(IdentityService.CombinedIdentity(configuration));
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
            DrawTextInput("Export folder", "Folder where Penumbra exported .pmp files are saved", ref penumbraExportFolder, 1024);
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

        if (DrawTextInput("PMP file to send", @"C:\path\to\mod.pmp", ref uploadPlaintextPath, 1024))
        {
            configuration.LastUploadPath = uploadPlaintextPath;
            configuration.Save();
        }

        DrawTextInput("Transfer passphrase", "Local password used to encrypt this upload", ref uploadPassphrase, 512, ImGuiInputTextFlags.Password);
        var selectedContact = DrawContactSelector();
        if (selectedContact is not null)
        {
            ImGui.TextUnformatted($"Selected contact: {selectedContact.DisplayName} ({selectedContact.PmpShareId})");
            if (string.IsNullOrWhiteSpace(selectedContact.PublicKeyBase64))
            {
                ImGui.TextWrapped("This contact is missing a public key. Paste their combined identity in Contacts before creating a send request.");
            }
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
        DrawTextInput("Transfer ID", "32-character code from a manual share", ref downloadTransferId, 128);
        if (DrawTextInput("Save decrypted PMP to", @"C:\Users\you\Desktop\PmpShare", ref downloadDirectory, 1024))
        {
            configuration.LastDownloadDirectory = downloadDirectory;
            configuration.Save();
        }

        DrawTextInput("Transfer passphrase", "Required only for manual transfer IDs", ref downloadPassphrase, 512, ImGuiInputTextFlags.Password);
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
        DrawTextInput("Display name", "Friendly name for this contact", ref contactName, 128);
        DrawTextInput("PmpShare identity", "Paste ps_xxx.publicKeyBase64 from their Status tab", ref contactIdentity, 256);
        if (ImGui.Button("Add contact"))
        {
            if (string.IsNullOrWhiteSpace(contactName) || string.IsNullOrWhiteSpace(contactIdentity))
            {
                contactResult = "Display name and PmpShare identity are required.";
            }
            else if (!IdentityService.TryParseCombinedIdentity(contactIdentity, out var pmpShareId, out var publicKeyBase64))
            {
                contactResult = "Paste an identity in the format ps_xxx.publicKeyBase64.";
            }
            else
            {
                configuration.Contacts.Add(new Contact
                {
                    DisplayName = contactName.Trim(),
                    PmpShareId = pmpShareId,
                    PublicKeyBase64 = publicKeyBase64,
                });
                configuration.Save();
                contactName = string.Empty;
                contactIdentity = string.Empty;
                contactResult = "Contact added.";
            }
        }

        ImGui.TextWrapped(contactResult);
        ImGui.Separator();
        for (var i = 0; i < configuration.Contacts.Count; i++)
        {
            var contact = configuration.Contacts[i];
            ImGui.TextUnformatted($"{contact.DisplayName} - {contact.PmpShareId}");
            if (!string.IsNullOrWhiteSpace(contact.PublicKeyBase64))
            {
                ImGui.TextWrapped($"Public key: {contact.PublicKeyBase64}");
            }
            else
            {
                ImGui.TextWrapped("Missing public key. Remove and re-add this contact with their combined identity.");
            }
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
        if (DrawTextInput("API base URL", "https://pmpshare-api.contact-theankh.workers.dev", ref apiBaseUrl, 512))
        {
            configuration.ApiBaseUrl = apiBaseUrl.Trim();
            configuration.Save();
        }

        DrawTextInput("Tester key", "Private Worker tester key; kept in memory only", ref testerKey, 512, ImGuiInputTextFlags.Password);

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

        if (DrawTextInput("Penumbra export folder", "Folder where Penumbra writes exported .pmp files", ref penumbraExportFolder, 1024))
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
            if (ImGui.Selectable(FormatModDisplayName(directory, name), selectedModDirectory == directory))
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
        if (string.IsNullOrWhiteSpace(contact.PublicKeyBase64))
        {
            uploadResult = "Selected contact is missing a public key. Re-add them using their combined identity.";
            return;
        }
        await EncryptAndUploadAsync(cancellationToken).ConfigureAwait(false);
        if (!IsTransferId(lastTransferId))
        {
            uploadResult = "Upload failed before a send request could be created.";
            return;
        }

        var wrappedPassphrase = IdentityService.WrapPassphrase(
            configuration.X25519PrivateKeyBase64,
            contact.PublicKeyBase64,
            uploadPassphrase);

        var request = await apiClient.CreateSendRequestAsync(
            apiBaseUrl,
            testerKey,
            new CreateSendRequestRequest(
                configuration.PmpShareId,
                contact.PmpShareId,
                "PmpShare tester",
                lastTransferId,
                configuration.X25519PublicKeyBase64,
                wrappedPassphrase.CiphertextBase64,
                wrappedPassphrase.NonceBase64,
                "Encrypted PmpShare transfer",
                10_800),
            cancellationToken).ConfigureAwait(false);
        uploadResult = $"Send request created: {request.RequestId}. Receiver can accept and decrypt automatically.";
    }

    private async Task RefreshInboxAsync(CancellationToken cancellationToken)
    {
        inbox = (await apiClient.GetInboxAsync(apiBaseUrl, testerKey, configuration.PmpShareId, cancellationToken).ConfigureAwait(false)).ToList();
        receiveResult = $"Loaded {inbox.Count} inbox request(s).";
    }

    private async Task AcceptRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        var accepted = await apiClient.AcceptSendRequestAsync(apiBaseUrl, testerKey, request.RequestId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accepted.TransferId) ||
            string.IsNullOrWhiteSpace(accepted.SenderPublicKey) ||
            string.IsNullOrWhiteSpace(accepted.EncryptedPassphrase) ||
            string.IsNullOrWhiteSpace(accepted.EncryptedPassphraseNonce))
        {
            receiveResult = "Request accepted, but it did not include an encrypted passphrase envelope.";
            await RefreshInboxAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        downloadTransferId = accepted.TransferId;
        downloadPassphrase = IdentityService.UnwrapPassphrase(
            configuration.X25519PrivateKeyBase64,
            accepted.SenderPublicKey,
            accepted.EncryptedPassphrase,
            accepted.EncryptedPassphraseNonce);
        receiveResult = "Request accepted. Passphrase unwrapped locally; downloading transfer...";
        await DownloadDecryptAndVerifyAsync(cancellationToken).ConfigureAwait(false);
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
        var contact = configuration.Contacts[Math.Clamp(selectedContactIndex, 0, configuration.Contacts.Count - 1)];
        if (string.IsNullOrWhiteSpace(contact.PublicKeyBase64))
        {
            uploadResult = "Selected contact is missing a public key.";
            return false;
        }
        if (!ValidateUploadInputs())
        {
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

    private static bool DrawTextInput(
        string label,
        string hint,
        ref string value,
        int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ImGui.TextUnformatted(label);
        ImGui.TextDisabled(hint);
        ImGui.SetNextItemWidth(-1);
        var changed = flags == ImGuiInputTextFlags.None
            ? ImGui.InputText($"##{label}", ref value, maxLength)
            : ImGui.InputText($"##{label}", ref value, maxLength, flags);
        return changed;
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

    private static string FormatModDisplayName(string directory, string name)
    {
        var cleanedDirectory = StripKnownModSuffix(directory);
        if (string.IsNullOrWhiteSpace(name))
        {
            return cleanedDirectory;
        }

        if (NormalizeName(cleanedDirectory) == NormalizeName(name))
        {
            return name;
        }

        return $"{name} ({cleanedDirectory})";
    }

    private static string StripKnownModSuffix(string value)
    {
        var trimmed = value.Trim();
        foreach (var suffix in new[] { ".pmp", ".ttmp2", ".ttmp" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^suffix.Length];
            }
        }

        return trimmed;
    }

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
