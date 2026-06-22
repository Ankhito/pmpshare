import { blobPath } from "./paths";
import { isExpired, saveMetadata } from "./metadata";
import type { Env, TransferMetadata } from "./types";

export async function cleanupExpiredTransfers(env: Env): Promise<void> {
  let cursor: string | undefined;

  do {
    const page = await env.PMP_BUCKET.list({ prefix: "transfers/", cursor });
    cursor = page.truncated ? page.cursor : undefined;

    const metadataObjects = page.objects.filter((object) =>
      object.key.endsWith("/metadata.json"),
    );
    await Promise.all(
      metadataObjects.map((object) => expireIfNeeded(env, object.key)),
    );
  } while (cursor);
}

async function expireIfNeeded(env: Env, metadataKey: string): Promise<void> {
  const object = await env.PMP_BUCKET.get(metadataKey);
  if (!object) {
    return;
  }

  const metadata = await object.json<TransferMetadata>();
  if (
    !isExpired(metadata) ||
    metadata.status === "expired" ||
    metadata.status === "completed" ||
    metadata.status === "deleted"
  ) {
    return;
  }

  await env.PMP_BUCKET.delete(blobPath(metadata.transferId));
  await saveMetadata(env, {
    ...metadata,
    status: "expired",
    blobDeleted: true,
    expiredAt: new Date().toISOString(),
  });
}
