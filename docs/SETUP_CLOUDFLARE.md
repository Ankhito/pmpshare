# Cloudflare Setup

These steps assume the R2 bucket is named `pmpshare-mvp`.

To change the bucket name later, edit `server/pmpshare-api/wrangler.jsonc`:

```jsonc
"r2_buckets": [
  {
    "binding": "PMP_BUCKET",
    "bucket_name": "pmpshare-mvp"
  }
]
```

## 1. Install Prerequisites

Install Node.js LTS from:

https://nodejs.org/

Then open a terminal at the repo root:

```powershell
cd C:\Users\papou\Documents\pmpshare
cd server\pmpshare-api
npm install
```

## 2. Log In To Cloudflare

```powershell
npx wrangler login
```

## 3. Confirm Or Create The R2 Bucket

List buckets:

```powershell
npx wrangler r2 bucket list
```

If `pmpshare-mvp` does not exist, create it:

```powershell
npx wrangler r2 bucket create pmpshare-mvp
```

The Worker binding is already configured in `server/pmpshare-api/wrangler.jsonc`:

```jsonc
"r2_buckets": [
  {
    "binding": "PMP_BUCKET",
    "bucket_name": "pmpshare-mvp"
  }
]
```

## 4. Configure Secrets

For deployed Cloudflare:

```powershell
npx wrangler secret put PMPSHARE_TESTER_KEY
```

Paste a private tester key when prompted. Do not commit real secrets.

For local development:

```powershell
Copy-Item .dev.vars.example .dev.vars
notepad .dev.vars
```

Replace `replace-me` with your tester key.

## 5. Run Locally

```powershell
npm run dev
```

Wrangler will print a local URL, usually `http://127.0.0.1:8787`.

Test health:

```powershell
Invoke-RestMethod http://127.0.0.1:8787/health
```

## 6. Test Upload Flow Locally

Set variables:

```powershell
$BaseUrl = "http://127.0.0.1:8787"
$TesterKey = "replace-with-your-local-key"
```

Create a small encrypted test blob placeholder:

```powershell
"encrypted-by-client-placeholder" | Set-Content -NoNewline .\sample.pmp.enc
$Size = (Get-Item .\sample.pmp.enc).Length
```

Create a transfer:

```powershell
$CreateBody = @{
  fileName = "example.pmp"
  plaintextSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  encryptedSize = $Size
  expiresInSeconds = 10800
} | ConvertTo-Json

$Transfer = Invoke-RestMethod `
  -Method Post `
  -Uri "$BaseUrl/v1/transfers" `
  -Headers @{ "X-PmpShare-Key" = $TesterKey } `
  -ContentType "application/json" `
  -Body $CreateBody

$Transfer
```

Upload the encrypted blob:

```powershell
Invoke-RestMethod `
  -Method Put `
  -Uri "$BaseUrl$($Transfer.uploadUrl)" `
  -Headers @{ "X-PmpShare-Key" = $TesterKey } `
  -InFile .\sample.pmp.enc `
  -ContentType "application/octet-stream"
```

Read metadata:

```powershell
Invoke-RestMethod "$BaseUrl$($Transfer.metadataUrl)"
```

Download the encrypted blob:

```powershell
Invoke-WebRequest `
  -Uri "$BaseUrl/v1/transfers/$($Transfer.transferId)/blob" `
  -OutFile .\downloaded.pmp.enc
```

Mark complete after local decrypt and plaintext SHA-256 verification:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "$BaseUrl/v1/transfers/$($Transfer.transferId)/complete" `
  -Headers @{ "X-PmpShare-Key" = $TesterKey }
```

## 7. Deploy

```powershell
npm run deploy
```

After deploy, Wrangler prints the Worker URL. Test:

```powershell
Invoke-RestMethod https://YOUR-WORKER.YOUR-SUBDOMAIN.workers.dev/health
```

## 8. Scheduled Cleanup

Scheduled cleanup is configured in `wrangler.jsonc`:

```jsonc
"triggers": {
  "crons": ["*/15 * * * *"]
}
```

The scheduled handler scans transfer metadata, deletes expired blobs, and marks expired metadata as `expired`.

## What Theodore Needs To Run Manually

### Local

Install Node.js LTS if it is missing:

https://nodejs.org/

Then run:

```powershell
cd C:\Users\papou\Documents\pmpshare\server\pmpshare-api
npm install
Copy-Item .dev.vars.example .dev.vars
notepad .dev.vars
npx wrangler dev
```

Paste this into `.dev.vars`, replacing the value with your local tester key:

```text
PMPSHARE_TESTER_KEY=replace-me
```

In a second terminal, run:

```powershell
cd C:\Users\papou\Documents\pmpshare\server\pmpshare-api
.\scripts\test-local.ps1 -BaseUrl "http://localhost:8787" -TesterKey "test-secret"
```

Use the same tester key in `.dev.vars` and in the script command.

### Cloudflare

Run:

```powershell
cd C:\Users\papou\Documents\pmpshare\server\pmpshare-api
npx wrangler login
npx wrangler r2 bucket list
npx wrangler r2 bucket create pmpshare-mvp
npx wrangler secret put PMPSHARE_TESTER_KEY
npx wrangler deploy
```

Only run `npx wrangler r2 bucket create pmpshare-mvp` if the bucket does not already exist.

After deploy, test the remote Worker:

```powershell
.\scripts\test-remote.ps1 -BaseUrl "https://pmpshare-api.YOUR.workers.dev" -TesterKey "real-secret"
```
