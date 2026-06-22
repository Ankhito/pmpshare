import {
  DEFAULT_EXPIRES_IN_SECONDS,
  MAX_DOWNLOADS,
  SERVICE_NAME,
} from "./constants";
import { createTransferId, isValidTransferId } from "./ids";
import { blobPath } from "./paths";
import {
  deleteBlob,
  loadMetadata,
  markExpiredIfNeeded,
  publicMetadata,
  saveMetadata,
} from "./metadata";
import { errorResponse, jsonResponse } from "./responses";
import type { Env, TransferMetadata } from "./types";
import { validateCreateTransferBody } from "./validation";

export async function health(): Promise<Response> {
  return jsonResponse({ ok: true, service: SERVICE_NAME });
}

export async function createTransfer(
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

  let input;
  try {
    input = validateCreateTransferBody(body);
  } catch (error) {
    return errorResponse(
      400,
      error instanceof Error ? error.message : "Invalid request body.",
      "invalid_request",
    );
  }

  const now = new Date();
  const transferId = createTransferId();
  const expiresAt = new Date(
    now.getTime() + input.expiresInSeconds * 1000,
  ).toISOString();
  const metadata: TransferMetadata = {
    transferId,
    fileName: input.fileName,
    plaintextSha256: input.plaintextSha256,
    encryptedSize: input.encryptedSize,
    createdAt: now.toISOString(),
    expiresAt,
    status: "created",
    downloadCount: 0,
    maxDownloads: MAX_DOWNLOADS,
  };

  await saveMetadata(env, metadata);

  return jsonResponse(
    {
      transferId,
      uploadUrl: `/v1/transfers/${transferId}/blob`,
      metadataUrl: `/v1/transfers/${transferId}/metadata`,
      expiresAt,
    },
    { status: 201 },
  );
}

export async function uploadBlob(
  request: Request,
  env: Env,
  transferId: string,
): Promise<Response> {
  const metadata = await getActiveMetadata(env, transferId);
  if (metadata instanceof Response) {
    return metadata;
  }

  if (metadata.status !== "created") {
    return errorResponse(
      409,
      "Transfer is not accepting uploads.",
      "invalid_transfer_status",
    );
  }

  const contentLength = request.headers.get("content-length");
  if (!contentLength) {
    return errorResponse(
      411,
      "Blob upload requires a Content-Length header.",
      "length_required",
    );
  }

  if (
    !Number.isSafeInteger(Number(contentLength)) ||
    Number(contentLength) !== metadata.encryptedSize
  ) {
    return errorResponse(
      400,
      "Uploaded blob size must match encryptedSize metadata.",
      "size_mismatch",
    );
  }

  await env.PMP_BUCKET.put(blobPath(transferId), request.body, {
    httpMetadata: {
      contentType: "application/octet-stream",
    },
  });

  const uploaded: TransferMetadata = {
    ...metadata,
    status: "uploaded",
    uploadedAt: new Date().toISOString(),
  };
  await saveMetadata(env, uploaded);

  return jsonResponse(publicMetadata(uploaded));
}

export async function getMetadata(
  env: Env,
  transferId: string,
): Promise<Response> {
  const metadata = await loadMetadata(env, transferId);
  if (!metadata) {
    return errorResponse(404, "Transfer was not found.", "not_found");
  }

  const current = await markExpiredIfNeeded(env, metadata);
  return jsonResponse(publicMetadata(current));
}

export async function downloadBlob(
  env: Env,
  transferId: string,
): Promise<Response> {
  const metadata = await getActiveMetadata(env, transferId);
  if (metadata instanceof Response) {
    return metadata;
  }

  if (metadata.status !== "uploaded") {
    return errorResponse(
      409,
      "Transfer blob is not available for download.",
      "invalid_transfer_status",
    );
  }

  if (metadata.downloadCount >= metadata.maxDownloads) {
    return errorResponse(
      409,
      "Transfer download limit has been reached.",
      "download_limit_reached",
    );
  }

  const object = await env.PMP_BUCKET.get(blobPath(transferId));
  if (!object) {
    return errorResponse(404, "Transfer blob was not found.", "blob_not_found");
  }

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("etag", object.httpEtag);
  headers.set("content-type", "application/octet-stream");
  headers.set("content-length", String(object.size));
  headers.set(
    "content-disposition",
    `attachment; filename="${metadata.fileName.replace(/"/g, "")}"`,
  );

  return new Response(object.body, { headers });
}

export async function completeTransfer(
  env: Env,
  transferId: string,
): Promise<Response> {
  const metadata = await getActiveMetadata(env, transferId);
  if (metadata instanceof Response) {
    return metadata;
  }

  if (metadata.status !== "uploaded") {
    return errorResponse(
      409,
      "Only uploaded transfers can be completed.",
      "invalid_transfer_status",
    );
  }

  await deleteBlob(env, transferId);
  const completed: TransferMetadata = {
    ...metadata,
    status: "completed",
    downloadCount: Math.min(metadata.downloadCount + 1, metadata.maxDownloads),
    blobDeleted: true,
    completedAt: new Date().toISOString(),
  };
  await saveMetadata(env, completed);

  return jsonResponse(publicMetadata(completed));
}

export async function deleteTransfer(
  env: Env,
  transferId: string,
): Promise<Response> {
  const metadata = await loadMetadata(env, transferId);
  if (!metadata) {
    return errorResponse(404, "Transfer was not found.", "not_found");
  }

  await deleteBlob(env, transferId);
  const deleted: TransferMetadata = {
    ...metadata,
    status: "deleted",
    blobDeleted: true,
    deletedAt: new Date().toISOString(),
  };
  await saveMetadata(env, deleted);

  return jsonResponse(publicMetadata(deleted));
}

async function getActiveMetadata(
  env: Env,
  transferId: string,
): Promise<TransferMetadata | Response> {
  if (!isValidTransferId(transferId)) {
    return errorResponse(400, "Invalid transfer id.", "invalid_transfer_id");
  }

  const metadata = await loadMetadata(env, transferId);
  if (!metadata) {
    return errorResponse(404, "Transfer was not found.", "not_found");
  }

  const current = await markExpiredIfNeeded(env, metadata);
  if (current.status === "expired" || isExpiredStatus(current.status)) {
    return errorResponse(
      410,
      "Transfer has expired or is no longer available.",
      "gone",
    );
  }

  return current;
}

function isExpiredStatus(status: string): boolean {
  return status === "completed" || status === "deleted";
}
