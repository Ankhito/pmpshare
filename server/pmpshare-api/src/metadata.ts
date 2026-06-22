import { blobPath, metadataPath } from "./paths";
import type { Env, TransferMetadata } from "./types";

export async function loadMetadata(
  env: Env,
  transferId: string,
): Promise<TransferMetadata | null> {
  const object = await env.PMP_BUCKET.get(metadataPath(transferId));
  if (!object) {
    return null;
  }

  return object.json<TransferMetadata>();
}

export async function saveMetadata(
  env: Env,
  metadata: TransferMetadata,
): Promise<void> {
  await env.PMP_BUCKET.put(
    metadataPath(metadata.transferId),
    JSON.stringify(metadata, null, 2),
    {
      httpMetadata: {
        contentType: "application/json; charset=utf-8",
      },
    },
  );
}

export async function deleteBlob(env: Env, transferId: string): Promise<void> {
  await env.PMP_BUCKET.delete(blobPath(transferId));
}

export function isExpired(
  metadata: TransferMetadata,
  now = new Date(),
): boolean {
  return new Date(metadata.expiresAt).getTime() <= now.getTime();
}

export function publicMetadata(metadata: TransferMetadata): TransferMetadata {
  return {
    transferId: metadata.transferId,
    fileName: metadata.fileName,
    plaintextSha256: metadata.plaintextSha256,
    encryptedSize: metadata.encryptedSize,
    createdAt: metadata.createdAt,
    expiresAt: metadata.expiresAt,
    status: metadata.status,
    downloadCount: metadata.downloadCount,
    maxDownloads: metadata.maxDownloads,
    blobDeleted: metadata.blobDeleted,
    uploadedAt: metadata.uploadedAt,
    completedAt: metadata.completedAt,
    deletedAt: metadata.deletedAt,
    expiredAt: metadata.expiredAt,
  };
}

export async function markExpiredIfNeeded(
  env: Env,
  metadata: TransferMetadata,
): Promise<TransferMetadata> {
  if (
    !isExpired(metadata) ||
    metadata.status === "expired" ||
    metadata.status === "completed" ||
    metadata.status === "deleted"
  ) {
    return metadata;
  }

  await deleteBlob(env, metadata.transferId);
  const expired: TransferMetadata = {
    ...metadata,
    status: "expired",
    blobDeleted: true,
    expiredAt: new Date().toISOString(),
  };
  await saveMetadata(env, expired);
  return expired;
}
