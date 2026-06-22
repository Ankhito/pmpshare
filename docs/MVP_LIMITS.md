# MVP Limits

PmpShare currently targets a narrow temporary transfer MVP.

## Transfer Limits

- Maximum encrypted blob size: 500 MB.
- Default expiry: 3 hours.
- Maximum expiry: 3 hours.
- Max downloads: 1.
- Blob deletion happens after confirmed receive via `/complete`.
- Expired blobs are deleted by scheduled cleanup.

## Safety Boundaries

- No plaintext `.pmp` handling server-side.
- No decryption keys server-side.
- No permanent mod hosting.
- No public file browsing.
- No public search.
- No accounts yet.
- No XIVAuth yet.
- No Dalamud plugin yet.
- No Penumbra integration yet.
- No auto-import.
- No auto-enable.
- No game packet logic.

## Current Auth

The MVP uses a private tester key header:

```http
X-PmpShare-Key: <tester key>
```

This is intentionally simple and should be replaced before broader use.

## Free-Tier And Abuse Risk

Cloudflare free-tier and R2 limits can still be exhausted by large uploads, repeated downloads, or leaked tester keys. Before opening access beyond trusted testers, add stronger abuse prevention such as per-user auth, rate limits, quotas, audit logging, and key rotation.

## Download Counting

The MVP does not increment `downloadCount` during `GET /v1/transfers/{id}/blob`, because failed or interrupted downloads should be retryable. `downloadCount` increments when the receiver calls `/complete` after local decrypt and plaintext SHA-256 verification.
