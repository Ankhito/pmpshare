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

## What It Does Not Do Yet

- No Penumbra import.
- No auto-enable.
- No Mare-style sync.
- No XIVAuth.
- No public browsing or search.
- No permanent hosting.
- No game packet logic.

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
3. Upload tab: choose a `.pmp`, enter a passphrase, then click `Encrypt and upload`.
4. Share the transfer ID and passphrase privately.
5. Download tab: paste the transfer ID and passphrase, choose an output directory, then click `Download and complete`.

The tester key is kept in memory only and is not saved in plugin configuration. The passphrase is never sent to the Worker.
