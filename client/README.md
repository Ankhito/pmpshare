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
- Can list, search, package, and send installed Penumbra mods without requiring a manual export first.
- Keeps the main tabs pinned while long tab content scrolls underneath.
- Creates a local PmpShare identity with a `ps_...` ID and X25519 keypair.
- Copies a one-paste identity string in the format `ps_....publicKeyBase64`.
- Keeps a separate editable display name for send-request metadata; the identity itself stays account-level.
- Can create and view Worker-backed send requests for manually added contacts.
- Polls for pending inbox requests, backs off to hourly checks after idle empty polls, and prints a chat notification when a new receive request appears.
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

There is no verified public Penumbra export IPC in the installed API docs. Penumbra's own export button creates a `.pmp` by zipping the mod folder while skipping `.bak` files, so PmpShare mirrors that behavior into a temporary package:

1. Select the installed mod in PmpShare.
2. PmpShare writes a temporary `.pmp` package in the system temp folder.
3. PmpShare encrypts and uploads that package.
4. PmpShare deletes the temporary package after the send attempt.

PmpShare does not modify the original Penumbra mod folder.

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
