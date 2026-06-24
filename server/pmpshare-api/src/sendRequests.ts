import { createTransferId } from "./ids";
import { sendRequestPath } from "./paths";
import { errorResponse, jsonResponse } from "./responses";
import type { Env, SendRequestMetadata } from "./types";

const DEFAULT_SEND_REQUEST_EXPIRY_SECONDS = 3 * 60 * 60;
const MAX_SEND_REQUEST_EXPIRY_SECONDS = 24 * 60 * 60;

export async function createSendRequest(
  request: Request,
  env: Env,
): Promise<Response> {
  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return errorResponse(
      400,
      "Request body must be valid JSON.",
      "invalid_json",
    );
  }

  let input: CreateSendRequestInput;
  try {
    input = validateCreateSendRequest(body);
  } catch (error) {
    return errorResponse(
      400,
      error instanceof Error ? error.message : "Invalid request body.",
      "invalid_request",
    );
  }

  const now = new Date();
  const requestId = createTransferId();
  const expiresAt = new Date(
    now.getTime() + input.expiresInSeconds * 1000,
  ).toISOString();
  const metadata: SendRequestMetadata = {
    requestId,
    senderId: input.senderId,
    recipientId: input.recipientId,
    senderDisplayName: input.senderDisplayName,
    transferId: input.transferId,
    senderPublicKey: input.senderPublicKey,
    encryptedPassphrase: input.encryptedPassphrase,
    encryptedPassphraseNonce: input.encryptedPassphraseNonce,
    message: input.message,
    createdAt: now.toISOString(),
    expiresAt,
    status: "pending",
  };

  await saveSendRequest(env, metadata);
  return jsonResponse(metadata, { status: 201 });
}

export async function getInbox(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const recipientId = url.searchParams.get("recipientId")?.trim() ?? "";
  if (!isValidPmpShareId(recipientId)) {
    return errorResponse(
      400,
      "recipientId must be a valid PmpShare ID.",
      "invalid_recipient_id",
    );
  }

  const items: SendRequestMetadata[] = [];
  let cursor: string | undefined;
  do {
    const page = await env.PMP_BUCKET.list({
      prefix: "send-requests/",
      cursor,
    });
    cursor = page.truncated ? page.cursor : undefined;
    for (const object of page.objects) {
      const metadata = await loadSendRequest(env, object.key);
      if (!metadata || metadata.recipientId !== recipientId) {
        continue;
      }
      items.push(await markExpiredIfNeeded(env, metadata));
    }
  } while (cursor);

  items.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  return jsonResponse({ requests: items });
}

export async function acceptSendRequest(
  env: Env,
  requestId: string,
): Promise<Response> {
  return updateSendRequestStatus(env, requestId, "accepted");
}

export async function declineSendRequest(
  env: Env,
  requestId: string,
): Promise<Response> {
  return updateSendRequestStatus(env, requestId, "declined");
}

async function updateSendRequestStatus(
  env: Env,
  requestId: string,
  status: "accepted" | "declined",
): Promise<Response> {
  if (!/^[a-f0-9]{32}$/.test(requestId)) {
    return errorResponse(400, "Invalid send request id.", "invalid_request_id");
  }

  const metadata = await loadSendRequest(env, sendRequestPath(requestId));
  if (!metadata) {
    return errorResponse(404, "Send request was not found.", "not_found");
  }

  const current = await markExpiredIfNeeded(env, metadata);
  if (status === "accepted" && current.status === "accepted") {
    return jsonResponse(current);
  }

  if (current.status !== "pending" && !(status === "declined" && current.status === "accepted")) {
    return errorResponse(
      409,
      `Send request is already ${current.status}.`,
      "invalid_request_status",
    );
  }

  const now = new Date().toISOString();
  const updated: SendRequestMetadata = {
    ...current,
    status,
    acceptedAt: status === "accepted" ? now : current.acceptedAt,
    declinedAt: status === "declined" ? now : current.declinedAt,
  };
  await saveSendRequest(env, updated);
  return jsonResponse(updated);
}

async function loadSendRequest(
  env: Env,
  key: string,
): Promise<SendRequestMetadata | null> {
  const object = await env.PMP_BUCKET.get(key);
  if (!object) {
    return null;
  }
  return object.json<SendRequestMetadata>();
}

async function saveSendRequest(
  env: Env,
  metadata: SendRequestMetadata,
): Promise<void> {
  await env.PMP_BUCKET.put(
    sendRequestPath(metadata.requestId),
    JSON.stringify(metadata, null, 2),
    {
      httpMetadata: { contentType: "application/json; charset=utf-8" },
    },
  );
}

async function markExpiredIfNeeded(
  env: Env,
  metadata: SendRequestMetadata,
): Promise<SendRequestMetadata> {
  if (
    metadata.status !== "pending" ||
    new Date(metadata.expiresAt).getTime() > Date.now()
  ) {
    return metadata;
  }

  const expired: SendRequestMetadata = {
    ...metadata,
    status: "expired",
  };
  await saveSendRequest(env, expired);
  return expired;
}

interface CreateSendRequestInput {
  senderId: string;
  recipientId: string;
  senderDisplayName?: string;
  transferId?: string;
  senderPublicKey?: string;
  encryptedPassphrase?: string;
  encryptedPassphraseNonce?: string;
  message?: string;
  expiresInSeconds: number;
}

function validateCreateSendRequest(body: unknown): CreateSendRequestInput {
  if (!body || typeof body !== "object") {
    throw new Error("Request body must be a JSON object.");
  }
  const record = body as Record<string, unknown>;
  const senderId = stringField(record.senderId, "senderId");
  const recipientId = stringField(record.recipientId, "recipientId");
  if (!isValidPmpShareId(senderId) || !isValidPmpShareId(recipientId)) {
    throw new Error("senderId and recipientId must be valid PmpShare IDs.");
  }

  const transferId = optionalString(record.transferId, "transferId");
  if (transferId && !/^[a-f0-9]{32}$/.test(transferId)) {
    throw new Error("transferId must be a valid transfer id when provided.");
  }

  const senderPublicKey = optionalString(record.senderPublicKey, "senderPublicKey");
  const encryptedPassphrase = optionalString(record.encryptedPassphrase, "encryptedPassphrase");
  const encryptedPassphraseNonce = optionalString(
    record.encryptedPassphraseNonce,
    "encryptedPassphraseNonce",
  );
  const hasPassphraseEnvelope =
    senderPublicKey !== undefined ||
    encryptedPassphrase !== undefined ||
    encryptedPassphraseNonce !== undefined;
  if (hasPassphraseEnvelope) {
    if (!senderPublicKey || !encryptedPassphrase || !encryptedPassphraseNonce) {
      throw new Error(
        "senderPublicKey, encryptedPassphrase, and encryptedPassphraseNonce must be provided together.",
      );
    }
    if (!isBase64OfLength(senderPublicKey, 32)) {
      throw new Error("senderPublicKey must be a base64-encoded X25519 public key.");
    }
    if (!isBase64OfLength(encryptedPassphraseNonce, 12)) {
      throw new Error(
        "encryptedPassphraseNonce must be a base64-encoded AES-GCM nonce.",
      );
    }
    if (!isBase64InRange(encryptedPassphrase, 17, 256)) {
      throw new Error("encryptedPassphrase must be a valid base64 envelope.");
    }
  }

  let expiresInSeconds = DEFAULT_SEND_REQUEST_EXPIRY_SECONDS;
  if (record.expiresInSeconds !== undefined) {
    if (
      typeof record.expiresInSeconds !== "number" ||
      !Number.isSafeInteger(record.expiresInSeconds) ||
      record.expiresInSeconds <= 0
    ) {
      throw new Error(
        "expiresInSeconds must be a positive integer when provided.",
      );
    }
    expiresInSeconds = Math.min(
      record.expiresInSeconds,
      MAX_SEND_REQUEST_EXPIRY_SECONDS,
    );
  }

  return {
    senderId,
    recipientId,
    transferId,
    senderPublicKey,
    encryptedPassphrase,
    encryptedPassphraseNonce,
    senderDisplayName: optionalString(
      record.senderDisplayName,
      "senderDisplayName",
    ),
    message: optionalString(record.message, "message"),
    expiresInSeconds,
  };
}

function stringField(value: unknown, name: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`${name} must be a non-empty string.`);
  }
  return value.trim();
}

function optionalString(value: unknown, name: string): string | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }
  if (typeof value !== "string") {
    throw new Error(`${name} must be a string when provided.`);
  }
  const trimmed = value.trim();
  return trimmed.length === 0 ? undefined : trimmed.slice(0, 512);
}

function isValidPmpShareId(value: string): boolean {
  return /^ps_[A-Za-z0-9_-]{12,80}$/.test(value);
}

function isBase64OfLength(value: string, expectedDecodedLength: number): boolean {
  return decodedBase64Length(value) === expectedDecodedLength;
}

function isBase64InRange(
  value: string,
  minDecodedLength: number,
  maxDecodedLength: number,
): boolean {
  const length = decodedBase64Length(value);
  return length >= minDecodedLength && length <= maxDecodedLength;
}

function decodedBase64Length(value: string): number {
  try {
    return Uint8Array.from(atob(value), (character) =>
      character.charCodeAt(0),
    ).length;
  } catch {
    return -1;
  }
}
