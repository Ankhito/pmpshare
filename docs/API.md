# API

Base URL:

- Local: `http://127.0.0.1:8787`
- Deployed: your Cloudflare Worker URL

Authenticated MVP endpoints require:

```http
X-PmpShare-Key: <tester key>
```

Error responses use:

```json
{
  "error": {
    "code": "invalid_request",
    "message": "Readable error message."
  }
}
```

## GET /health

Auth: none.

Response:

```json
{
  "ok": true,
  "service": "pmpshare-api"
}
```

## POST /v1/transfers

Auth: tester key required.

Creates transfer metadata. The Worker does not receive plaintext `.pmp` data or decryption keys.

Request:

```json
{
  "fileName": "example.pmp",
  "plaintextSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "encryptedSize": 123456789,
  "expiresInSeconds": 10800
}
```

Validation:

- `fileName` must be a simple `.pmp` file name, not a path.
- `plaintextSha256` must be a 64-character SHA-256 hex string.
- `encryptedSize` must be a positive integer up to 500 MB.
- `expiresInSeconds` defaults to 10800 and is capped at 10800.

Response `201`:

```json
{
  "transferId": "0123456789abcdef0123456789abcdef",
  "uploadUrl": "/v1/transfers/0123456789abcdef0123456789abcdef/blob",
  "metadataUrl": "/v1/transfers/0123456789abcdef0123456789abcdef/metadata",
  "expiresAt": "2026-06-22T15:00:00.000Z"
}
```

## PUT /v1/transfers/{id}/blob

Auth: tester key required.

Uploads the encrypted blob to R2 at `transfers/{transferId}/blob.bin`.

Rules:

- Metadata must exist.
- Transfer must not be expired.
- Transfer status must be `created`.
- `Content-Length` is required and must match `encryptedSize`.
- Plaintext `.pmp` files and decryption keys must never be uploaded.

Response:

```json
{
  "transferId": "0123456789abcdef0123456789abcdef",
  "fileName": "example.pmp",
  "plaintextSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "encryptedSize": 123456789,
  "createdAt": "2026-06-22T12:00:00.000Z",
  "expiresAt": "2026-06-22T15:00:00.000Z",
  "status": "uploaded",
  "downloadCount": 0,
  "maxDownloads": 1,
  "uploadedAt": "2026-06-22T12:01:00.000Z"
}
```

## GET /v1/transfers/{id}/metadata

Auth: none for MVP.

Returns public-safe metadata for a receiver using a share code.

Response:

```json
{
  "transferId": "0123456789abcdef0123456789abcdef",
  "fileName": "example.pmp",
  "plaintextSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "encryptedSize": 123456789,
  "createdAt": "2026-06-22T12:00:00.000Z",
  "expiresAt": "2026-06-22T15:00:00.000Z",
  "status": "uploaded",
  "downloadCount": 0,
  "maxDownloads": 1
}
```

## GET /v1/transfers/{id}/blob

Auth: none for MVP.

Downloads the encrypted blob.

Rules:

- Metadata must exist.
- Transfer must not be expired.
- Blob must exist.
- Status must be `uploaded`.
- `downloadCount` must be less than `maxDownloads`.

MVP behavior: this endpoint does not increment `downloadCount`, so an interrupted download can be retried. The transfer is counted as used when `/complete` succeeds after receiver-side decrypt and SHA-256 verification.

Response:

- `200 application/octet-stream`
- Body is the encrypted blob.

## POST /v1/transfers/{id}/complete

Auth: tester key required.

Called by the receiver only after download finished, local decrypt succeeded, and plaintext SHA-256 matched.

Effects:

- Marks metadata `completed`.
- Sets `blobDeleted` to `true`.
- Sets `completedAt`.
- Deletes `transfers/{transferId}/blob.bin`.

Response:

```json
{
  "transferId": "0123456789abcdef0123456789abcdef",
  "fileName": "example.pmp",
  "plaintextSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "encryptedSize": 123456789,
  "createdAt": "2026-06-22T12:00:00.000Z",
  "expiresAt": "2026-06-22T15:00:00.000Z",
  "status": "completed",
  "downloadCount": 1,
  "maxDownloads": 1,
  "blobDeleted": true,
  "completedAt": "2026-06-22T12:03:00.000Z"
}
```

## DELETE /v1/transfers/{id}

Auth: tester key required.

Manual sender/tester delete.

Effects:

- Deletes blob if present.
- Marks metadata `deleted`.
- Sets `blobDeleted` to `true`.
- Sets `deletedAt`.

Response: public-safe metadata with `status: "deleted"`.

## POST /v1/send-requests

Auth: tester key required.

Creates a contact/inbox request. This does not contain plaintext file keys or plaintext passphrases. The client can include an encrypted passphrase envelope created locally with X25519 ECDH and AES-GCM so the recipient can unwrap it locally after accepting.

Request:

```json
{
  "senderId": "ps_sender123456789",
  "recipientId": "ps_recipient123456789",
  "senderDisplayName": "Sender",
  "transferId": "0123456789abcdef0123456789abcdef",
  "senderPublicKey": "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
  "encryptedPassphrase": "base64-ciphertext-plus-tag",
  "encryptedPassphraseNonce": "base64-12-byte-nonce",
  "message": "Encrypted PmpShare transfer",
  "expiresInSeconds": 10800
}
```

Validation:

- `senderId` and `recipientId` must be PmpShare IDs.
- `transferId`, when present, must be a 32-character lowercase hex transfer ID.
- `senderPublicKey`, `encryptedPassphrase`, and `encryptedPassphraseNonce` are optional for backwards compatibility, but if any one is present all three must be present.
- `senderPublicKey` must decode to a 32-byte X25519 public key.
- `encryptedPassphraseNonce` must decode to a 12-byte AES-GCM nonce.
- `encryptedPassphrase` is an opaque base64 envelope containing ciphertext plus tag.

Response `201`: send request metadata with `status: "pending"`.

## GET /v1/inbox?recipientId=...

Auth: tester key required.

Returns send requests addressed to a local PmpShare ID.

```json
{
  "requests": [
    {
      "requestId": "0123456789abcdef0123456789abcdef",
      "senderId": "ps_sender123456789",
      "recipientId": "ps_recipient123456789",
      "status": "pending"
    }
  ]
}
```

## POST /v1/send-requests/{id}/accept

Auth: tester key required.

Marks a pending send request accepted.

## POST /v1/send-requests/{id}/decline

Auth: tester key required.

Marks a pending send request declined.

## Scheduled Cleanup

Cloudflare calls the Worker scheduled handler every 15 minutes according to `wrangler.jsonc`.

Effects:

- Scans metadata under `transfers/`.
- Deletes expired blobs.
- Marks expired metadata `expired`.
- Sets `blobDeleted` and `expiredAt`.

## Common Status Codes

- `200` - success.
- `201` - transfer created.
- `400` - invalid request, invalid transfer id, invalid JSON, or size mismatch.
- `401` - missing or invalid tester key.
- `404` - endpoint, metadata, or blob not found.
- `405` - method not allowed.
- `409` - transfer state conflict or download limit reached.
- `410` - transfer expired, completed, or deleted.
- `411` - blob upload is missing `Content-Length`.
- `500` - server misconfiguration or unexpected server error.
