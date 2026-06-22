export interface Env {
  PMP_BUCKET: R2Bucket;
  PMPSHARE_TESTER_KEY: string;
}

export type TransferStatus =
  | "created"
  | "uploaded"
  | "completed"
  | "deleted"
  | "expired";

export interface TransferMetadata {
  transferId: string;
  fileName: string;
  plaintextSha256: string;
  encryptedSize: number;
  createdAt: string;
  expiresAt: string;
  status: TransferStatus;
  downloadCount: number;
  maxDownloads: number;
  blobDeleted?: boolean;
  uploadedAt?: string;
  completedAt?: string;
  deletedAt?: string;
  expiredAt?: string;
}
