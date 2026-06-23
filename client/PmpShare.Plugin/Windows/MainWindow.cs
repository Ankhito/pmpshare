using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PmpShare.Plugin.Models;
using PmpShare.Plugin.Services;

namespace PmpShare.Plugin.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const long MaxEncryptedSizeBytes = 500L * 1024L * 1024L;
    private const int TransferIdLength = 32;
    private const string ApiBaseUrl = "https://pmpshare-api.contact-theankh.workers.dev";
    private const string TesterKey = "clubnoiristhebest";

    private readonly Configuration configuration;
    private readonly PmpShareApiClient apiClient;
    private readonly TransferCrypto transferCrypto;
    private readonly PenumbraIpcService penumbra;
    private readonly FileDialogManager fileDialogManager = new();

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
    private bool showManualSend;
    private bool showManualReceive;
    private bool verifiedReceiveReady;
    private bool isBusy;
    private float operationProgress;
    private string operationStage = string.Empty;
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
        fileDialogManager.Draw();
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
        var combinedIdentity = IdentityService.CombinedIdentity(configuration);
        ImGui.TextUnformatted("My PmpShare Identity");
        ImGui.TextWrapped(combinedIdentity);
        if (ImGui.Button("Copy My PmpShare Identity"))
        {
            ImGui.SetClipboardText(combinedIdentity);
        }

        if (ImGui.CollapsingHeader("Technical identity details"))
        {
            ImGui.TextUnformatted($"PmpShare ID: {configuration.PmpShareId}");
            ImGui.TextWrapped($"Public key: {configuration.X25519PublicKeyBase64}");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Worker: PmpShare testing API");
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
            ImGui.TextWrapped("PmpShare stages temporary encrypted upload files automatically. Penumbra can export mods in its UI, but the installed public Penumbra API does not expose an export IPC, so installed-mod sends need the folder where Penumbra writes exported .pmp files.");
            if (DrawPathInput("Penumbra export folder", "Folder where Penumbra writes exported .pmp files", ref penumbraExportFolder, 1024, "Browse##PenumbraExportFolder", () =>
                {
                    OpenFolderPicker("Select Penumbra export folder", penumbraExportFolder, selected =>
                    {
                        penumbraExportFolder = selected;
                        configuration.PenumbraExportFolder = selected;
                        configuration.Save();
                    });
                }))
            {
                configuration.PenumbraExportFolder = penumbraExportFolder;
                configuration.Save();
            }
            var deleteExportAfterUpload = configuration.DeletePenumbraExportAfterUpload;
            if (ImGui.Checkbox("Delete selected export after successful upload", ref deleteExportAfterUpload))
            {
                configuration.DeletePenumbraExportAfterUpload = deleteExportAfterUpload;
                configuration.Save();
            }
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

        if (DrawPathInput("PMP file to send", @"C:\path\to\mod.pmp", ref uploadPlaintextPath, 1024, "Browse##UploadPmp", () =>
            {
                OpenFilePicker("Select PMP file to send", ".pmp{.pmp}", uploadPlaintextPath, selected =>
                {
                    uploadPlaintextPath = selected;
                    configuration.LastUploadPath = selected;
                    configuration.Save();
                });
            }))
        {
            configuration.LastUploadPath = uploadPlaintextPath;
            configuration.Save();
        }

        var selectedContact = DrawContactSelector();
        if (selectedContact is not null)
        {
            ImGui.TextUnformatted($"Selected contact: {selectedContact.DisplayName} ({selectedContact.PmpShareId})");
            if (string.IsNullOrWhiteSpace(selectedContact.PublicKeyBase64))
            {
                ImGui.TextWrapped("This contact is missing a public key. Paste their combined identity in Contacts before creating a send request.");
            }
        }

        if (DrawActionButton("Send to Contact", CanStartOperation()) && ValidateSendRequestInputs())
        {
            StartOperation(async token => await CreateSendRequestAsync(token).ConfigureAwait(false));
        }

        ImGui.SameLine();
        if (DrawActionButton("Cancel", isBusy))
        {
            operationCts?.Cancel();
        }

        DrawOperationProgress();

        if (ImGui.CollapsingHeader("Advanced manual share"))
        {
            ImGui.Checkbox("Show manual upload/share-code tools", ref showManualSend);
            if (showManualSend)
            {
                ImGui.TextWrapped("Use this only for testing or fallback. The normal flow sends the encrypted passphrase to a contact request automatically.");
                DrawTextInput("Manual transfer passphrase", "Only used for manual share-code uploads", ref uploadPassphrase, 512, ImGuiInputTextFlags.Password);
                if (DrawActionButton("Upload / Share Code", CanStartOperation()) && ValidateUploadInputs())
                {
                    StartOperation(async token => await EncryptAndUploadAsync(uploadPassphrase, cleanupPenumbraExportAfterUpload: true, token).ConfigureAwait(false));
                }

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
        }

        ImGui.Separator();
        ImGui.TextWrapped(uploadResult);
    }

    private void DrawReceiveTab()
    {
        if (DrawPathInput("Save decrypted PMP to", @"C:\Users\you\Desktop\PmpShare", ref downloadDirectory, 1024, "Browse##DownloadDirectory", () =>
            {
                OpenFolderPicker("Select receive folder", downloadDirectory, selected =>
                {
                    downloadDirectory = selected;
                    configuration.LastDownloadDirectory = selected;
                    configuration.Save();
                });
            }))
        {
            configuration.LastDownloadDirectory = downloadDirectory;
            configuration.Save();
        }

        if (DrawActionButton("Refresh Inbox", CanStartOperation()))
        {
            StartOperation(async token => await RefreshInboxAsync(token).ConfigureAwait(false));
        }

        ImGui.SameLine();
        if (DrawActionButton("Cancel", isBusy))
        {
            operationCts?.Cancel();
        }

        DrawOperationProgress();

        if (ImGui.CollapsingHeader("Advanced manual receive"))
        {
            ImGui.Checkbox("Show manual transfer ID/passphrase tools", ref showManualReceive);
            if (showManualReceive)
            {
                DrawTextInput("Transfer ID", "32-character code from a manual share", ref downloadTransferId, 128);
                DrawTextInput("Transfer passphrase", "Required only for manual transfer IDs", ref downloadPassphrase, 512, ImGuiInputTextFlags.Password);
                if (DrawActionButton("Download / Decrypt / Verify", CanStartOperation()) && ValidateDownloadInputs())
                {
                    StartOperation(async token => await DownloadDecryptAndVerifyAsync(token).ConfigureAwait(false));
                }
            }
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
        ImGui.TextWrapped("Penumbra import is manual for now because Penumbra IPC only confirms that an import was queued, not that it fully succeeded.");
        ImGui.TextWrapped("Verified downloads are kept in the staging folder so import can be retried if Penumbra does not complete it.");
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

    private async Task EncryptAndUploadAsync(string passphrase, bool cleanupPenumbraExportAfterUpload, CancellationToken cancellationToken)
    {
        var sourcePath = uploadPlaintextPath.Trim('"', ' ');
        var encryptedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pmpshare.bin");
        try
        {
            SetOperationProgress("Encrypting file locally", 0.15f);
            uploadResult = "Encrypting locally...";
            var encryption = await transferCrypto.EncryptFileAsync(sourcePath, encryptedPath, passphrase, cancellationToken).ConfigureAwait(false);
            SetOperationProgress("Creating transfer metadata", 0.4f);
            var transfer = await apiClient.CreateTransferAsync(ApiBaseUrl, TesterKey, new CreateTransferRequest(Path.GetFileName(sourcePath), encryption.PlaintextSha256, encryption.EncryptedSize, 10_800), cancellationToken).ConfigureAwait(false);
            SetOperationProgress("Uploading encrypted blob", 0.65f);
            uploadResult = "Uploading encrypted blob...";
            await apiClient.UploadBlobAsync(ApiBaseUrl, TesterKey, transfer.UploadUrl, encryptedPath, cancellationToken).ConfigureAwait(false);
            lastTransferId = transfer.TransferId;
            lastMetadataUrl = new Uri(new Uri(ApiBaseUrl), transfer.MetadataUrl).ToString();
            SetOperationProgress("Upload complete", 1f);
            uploadResult = "Upload complete.";
            if (cleanupPenumbraExportAfterUpload)
            {
                TryDeleteUsedPenumbraExport(sourcePath);
            }
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
        SetOperationProgress("Fetching transfer metadata", 0.15f);
        receiveResult = "Fetching metadata...";
        var metadata = await apiClient.GetMetadataAsync(ApiBaseUrl, transferId, cancellationToken).ConfigureAwait(false);
        ValidateMetadata(metadata);

        var encryptedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pmpshare.bin");
        var finalPath = UniqueOutputPath(Path.Combine(downloadDirectory, metadata.FileName));
        var partPath = finalPath + ".part";
        try
        {
            SetOperationProgress("Downloading encrypted blob", 0.35f);
            receiveResult = "Downloading encrypted blob...";
            await apiClient.DownloadBlobAsync(ApiBaseUrl, transferId, encryptedPath, cancellationToken).ConfigureAwait(false);
            SetOperationProgress("Decrypting and verifying", 0.65f);
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
            SetOperationProgress("Completing transfer", 0.85f);
            await apiClient.CompleteTransferAsync(ApiBaseUrl, TesterKey, transferId, cancellationToken).ConfigureAwait(false);
            lastReceivedPmp = finalPath;
            verifiedReceiveReady = true;
            SetOperationProgress("Receive complete", 1f);
            receiveResult = $"Verified and saved: {finalPath}\nUse Import to Penumbra when ready.";
            if (!penumbra.CurrentStatus.IsAvailable)
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
        var transferSecret = CreateTransferSecret();
        var sourcePath = uploadPlaintextPath.Trim('"', ' ');
        await EncryptAndUploadAsync(transferSecret, cleanupPenumbraExportAfterUpload: false, cancellationToken).ConfigureAwait(false);
        if (!IsTransferId(lastTransferId))
        {
            uploadResult = "Upload failed before a send request could be created.";
            return;
        }

        var wrappedPassphrase = IdentityService.WrapPassphrase(
            configuration.X25519PrivateKeyBase64,
            contact.PublicKeyBase64,
            transferSecret);

        SetOperationProgress("Creating contact send request", 0.95f);
        var request = await apiClient.CreateSendRequestAsync(
            ApiBaseUrl,
            TesterKey,
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
        SetOperationProgress("Send request ready", 1f);
        uploadResult = $"Send request created: {request.RequestId}. Receiver can accept and decrypt automatically.";
        TryDeleteUsedPenumbraExport(sourcePath);
    }

    private async Task RefreshInboxAsync(CancellationToken cancellationToken)
    {
        await RefreshInboxAsync(cancellationToken, updateReceiveResult: true).ConfigureAwait(false);
    }

    private async Task RefreshInboxAsync(CancellationToken cancellationToken, bool updateReceiveResult)
    {
        SetOperationProgress("Refreshing inbox", 0.4f);
        inbox = (await apiClient.GetInboxAsync(ApiBaseUrl, TesterKey, configuration.PmpShareId, cancellationToken).ConfigureAwait(false)).ToList();
        SetOperationProgress("Inbox refreshed", 1f);
        if (updateReceiveResult)
        {
            receiveResult = $"Loaded {inbox.Count} inbox request(s).";
        }
        else
        {
            statusText = $"Inbox refreshed: {inbox.Count} request(s).";
        }
    }

    private async Task AcceptRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        SetOperationProgress("Accepting request", 0.15f);
        var accepted = await apiClient.AcceptSendRequestAsync(ApiBaseUrl, TesterKey, request.RequestId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accepted.TransferId) ||
            string.IsNullOrWhiteSpace(accepted.SenderPublicKey) ||
            string.IsNullOrWhiteSpace(accepted.EncryptedPassphrase) ||
            string.IsNullOrWhiteSpace(accepted.EncryptedPassphraseNonce))
        {
            receiveResult = "Request accepted, but it did not include an encrypted passphrase envelope.";
            await RefreshInboxAsync(cancellationToken, updateReceiveResult: false).ConfigureAwait(false);
            return;
        }

        downloadTransferId = accepted.TransferId;
        SetOperationProgress("Unwrapping passphrase locally", 0.25f);
        downloadPassphrase = IdentityService.UnwrapPassphrase(
            configuration.X25519PrivateKeyBase64,
            accepted.SenderPublicKey,
            accepted.EncryptedPassphrase,
            accepted.EncryptedPassphraseNonce);
        receiveResult = "Request accepted. Passphrase unwrapped locally; downloading transfer...";
        await DownloadDecryptAndVerifyAsync(cancellationToken).ConfigureAwait(false);
        downloadPassphrase = string.Empty;
        await RefreshInboxAsync(cancellationToken, updateReceiveResult: false).ConfigureAwait(false);
    }

    private async Task DeclineRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        SetOperationProgress("Declining request", 0.5f);
        await apiClient.DeclineSendRequestAsync(ApiBaseUrl, TesterKey, request.RequestId, cancellationToken).ConfigureAwait(false);
        SetOperationProgress("Request declined", 1f);
        await RefreshInboxAsync(cancellationToken, updateReceiveResult: false).ConfigureAwait(false);
        receiveResult = "Request declined.";
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
            if (result.QueuedForInstall)
            {
                receiveResult = $"Penumbra queued import with result code {result.ResultCode}. Kept verified .pmp so it can be retried if Penumbra import fails.";
            }
            else
            {
                receiveResult = $"Penumbra import was not queued. Result code: {result.ResultCode}. Kept verified .pmp: {lastReceivedPmp}";
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
        if (string.IsNullOrWhiteSpace(uploadPassphrase))
        {
            uploadResult = "Passphrase is required.";
            return false;
        }
        return true;
    }

    private bool ValidateSendRequestInputs()
    {
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
        if (!ValidateUploadPath())
        {
            return false;
        }
        return true;
    }

    private bool ValidateUploadPath()
    {
        var path = uploadPlaintextPath.Trim('"', ' ');
        if (!File.Exists(path) || !path.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase))
        {
            uploadResult = "Choose an existing .pmp file.";
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
        if (string.IsNullOrWhiteSpace(downloadDirectory) || string.IsNullOrWhiteSpace(downloadPassphrase))
        {
            receiveResult = "Output directory and passphrase are required.";
            return false;
        }
        return true;
    }

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

    private static string CreateTransferSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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

    private void DrawOperationProgress()
    {
        if (!isBusy && string.IsNullOrWhiteSpace(operationStage))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextWrapped(operationStage);
        ImGui.ProgressBar(operationProgress, new Vector2(-1, 0), $"{MathF.Round(operationProgress * 100f)}%");
    }

    private void SetOperationProgress(string stage, float progress)
    {
        operationStage = stage;
        operationProgress = Math.Clamp(progress, 0f, 1f);
        statusText = stage;
    }

    private void OpenFolderPicker(string title, string currentPath, Action<string> onSelected)
    {
        fileDialogManager.OpenFolderDialog(
            title,
            (success, selected) =>
            {
                if (success && !string.IsNullOrWhiteSpace(selected))
                {
                    onSelected(selected);
                }
            },
            StartDirectoryFor(currentPath),
            true);
    }

    private void OpenFilePicker(string title, string filters, string currentPath, Action<string> onSelected)
    {
        fileDialogManager.OpenFileDialog(
            title,
            filters,
            (success, selected) =>
            {
                if (success && selected.Count > 0 && !string.IsNullOrWhiteSpace(selected[0]))
                {
                    onSelected(selected[0]);
                }
            },
            1,
            StartDirectoryFor(currentPath),
            true);
    }

    private static string StartDirectoryFor(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            return directory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
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

    private static bool DrawPathInput(
        string label,
        string hint,
        ref string value,
        int maxLength,
        string buttonLabel,
        Action onBrowse)
    {
        ImGui.TextUnformatted(label);
        ImGui.TextDisabled(hint);
        var buttonWidth = ImGui.CalcTextSize("Browse").X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetNextItemWidth(-(buttonWidth + ImGui.GetStyle().ItemSpacing.X));
        var changed = ImGui.InputText($"##{label}", ref value, maxLength);
        ImGui.SameLine();
        if (ImGui.Button(buttonLabel))
        {
            onBrowse();
        }

        return changed;
    }

    private void StartOperation(Func<CancellationToken, Task> operation)
    {
        operationCts?.Dispose();
        operationCts = new CancellationTokenSource();
        isBusy = true;
        operationProgress = 0f;
        operationStage = "Starting...";
        _ = Task.Run(async () =>
        {
            try
            {
                await operation(operationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                statusText = "Operation cancelled.";
                operationStage = "Operation cancelled.";
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

    private void TryDeleteUsedPenumbraExport(string sourcePath)
    {
        if (!configuration.DeletePenumbraExportAfterUpload ||
            string.IsNullOrWhiteSpace(foundExportedPmp) ||
            !Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(foundExportedPmp), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDelete(foundExportedPmp);
        foundExportedPmp = string.Empty;
        uploadPlaintextPath = string.Empty;
        uploadResult += "\nDeleted selected Penumbra export after successful upload.";
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
