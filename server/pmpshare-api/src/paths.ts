export function metadataPath(transferId: string): string {
  return `transfers/${transferId}/metadata.json`;
}

export function blobPath(transferId: string): string {
  return `transfers/${transferId}/blob.bin`;
}
