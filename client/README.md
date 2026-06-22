# PmpShare Dalamud Plugin Client

This is the first client for the PmpShare relay API. It encrypts and decrypts locally, then uses the Cloudflare Worker API to upload and download only encrypted blobs.

Current command:

```text
/pmpshare
```

## What It Does

- Uploads a local `.pmp` file by encrypting it first with AES-GCM.
- Sends only encrypted bytes to the Worker/R2 API.
- Sends only the plaintext SHA-256 hash as transfer metadata, so the receiver can verify the decrypted result.
- Downloads encrypted blobs.
- Decrypts locally with the passphrase.
- Calls `/complete` only after decrypt succeeds and SHA-256 matches, which deletes the server blob.
- Can import a verified received `.pmp` through Penumbra IPC.
- Can list installed Penumbra mods and find matching exported `.pmp` files for sending.
- Creates a local PmpShare identity with a `ps_...` ID and X25519 keypair.
- Copies a one-paste identity string in the format `ps_....publicKeyBase64`.
- Can create and view Worker-backed send requests for manually added contacts.
- Wraps transfer passphrases locally with X25519 ECDH plus AES-GCM so accepted requests can download, decrypt, and verify without manual passphrase entry.

## What It Does Not Do Yet

- No auto-enable.
- No Mare-style sync.
- No XIVAuth.
- No public browsing or search.
- No permanent hosting.
- No game packet logic.
- No automatic contact discovery yet.
- No raw AES/decryption keys are stored on the Worker.
- The X25519 private key is stored only in local Dalamud plugin configuration.

## Penumbra IPC Limits

PmpShare uses Penumbra IPC for status, mod listing, mod paths, and `Penumbra.InstallMod.V5`. A successful install result means Penumbra queued the package for install; it does not guarantee the mod is fully installed yet.

PmpShare never auto-enables a Penumbra mod.

There is no verified public Penumbra export IPC in the installed API docs, so sending an installed Penumbra mod uses export-folder fallback:

1. Export the mod to `.pmp` through Penumbra.
2. Set the export folder in PmpShare settings.
3. Select the installed mod.
4. Click `Find Exported PMP`.
5. Send the detected `.pmp`.

PmpShare does not modify or package the original Penumbra mod folder.

## Install Cleanup

Receive writes decrypted files to staging as `.part`, verifies SHA-256, then renames to the final `.pmp`. The Worker `/complete` call happens only after decrypt and hash verification succeed.

If Penumbra import succeeds and `DeleteAfterSuccessfulPenumbraImport` is enabled, PmpShare deletes the local decrypted `.pmp`. If Penumbra is unavailable or import fails, the `.pmp` is kept.

## Build

From the repo root:

```powershell
dotnet build .\client\PmpShare.Plugin\PmpShare.Plugin.csproj
```

Current Dalamud development expects .NET 10 and a local XIVLauncher/Dalamud dev hook install.

## Test

Run the crypto roundtrip smoke test:

```powershell
dotnet run --project .\client\PmpShare.Plugin.Tests\PmpShare.Plugin.Tests.csproj
```

## MVP Usage

1. Run `/pmpshare` in game.
2. Paste the Worker URL and private tester key.
3. Status tab: copy your `PmpShare Identity` and share it privately with a contact.
4. Contacts tab: add a contact by pasting their combined `ps_....publicKeyBase64` identity.
5. Upload tab: choose a `.pmp`, enter a passphrase, pick a contact, then click `Create Send Request`.
6. Receive tab: click `Refresh Inbox`, then `Accept` to unwrap the passphrase locally, download, decrypt, verify, and complete the transfer.

The tester key is kept in memory only and is not saved in plugin configuration. The plaintext passphrase is never sent to the Worker; only an AES-GCM envelope encrypted for the recipient's public key is stored with the send request.
