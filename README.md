# PmpShare

PmpShare is an early prototype for temporary, encrypted `.pmp` file transfers.

Current milestone: a Cloudflare Worker relay API backed by Cloudflare R2, plus a testing-only Dalamud client MVP. The server only stores encrypted blobs and public-safe transfer metadata. Encryption and decryption happen locally in the client, so the Worker must never receive plaintext `.pmp` files or decryption keys.

PmpShare is not a Mare replacement, not a permanent mod host, and not a public browsing or search service. The Dalamud client is for private testing only and is not a normal Dalamud release.

The testing client can import verified received `.pmp` files through Penumbra IPC, but it never auto-enables mods. Installed Penumbra mods can be selected for sending only by finding an already exported `.pmp`; PmpShare does not fake an export IPC or modify Penumbra mod folders.

## Repository Layout

- `server/pmpshare-api` - Cloudflare Worker TypeScript transfer API.
- `client/PmpShare.Plugin` - Dalamud plugin client for encrypted upload/download.
- `docs/SETUP_CLOUDFLARE.md` - Cloudflare setup and test commands.
- `docs/API.md` - Endpoint documentation.
- `docs/MVP_LIMITS.md` - Current MVP limits and safety boundaries.

## Next Step

Use the testing plugin build against the deployed or local Worker API, then decide how share codes and tester access should work before any broader testing.

## What Theodore Needs To Run Manually

Local:

```powershell
cd C:\Users\papou\Documents\pmpshare\server\pmpshare-api
npm install
Copy-Item .dev.vars.example .dev.vars
notepad .dev.vars
npx wrangler dev
```

In a second terminal:

```powershell
cd C:\Users\papou\Documents\pmpshare\server\pmpshare-api
.\scripts\test-local.ps1 -BaseUrl "http://localhost:8787" -TesterKey "test-secret"
```

Cloudflare:

```powershell
cd C:\Users\papou\Documents\pmpshare\server\pmpshare-api
npx wrangler login
npx wrangler r2 bucket list
npx wrangler r2 bucket create pmpshare-mvp
npx wrangler secret put PMPSHARE_TESTER_KEY
npx wrangler deploy
.\scripts\test-remote.ps1 -BaseUrl "https://pmpshare-api.YOUR.workers.dev" -TesterKey "real-secret"
```

Install Node.js LTS first if `node` or `npm` is missing. Only create the R2 bucket if `npx wrangler r2 bucket list` does not show `pmpshare-mvp`.
