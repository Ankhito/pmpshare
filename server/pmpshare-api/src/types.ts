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

export type SendRequestStatus = "pending" | "accepted" | "declined" | "expired";

export interface SendRequestMetadata {
  requestId: string;
  senderId: string;
  recipientId: string;
  senderDisplayName?: string;
  transferId?: string;
  message?: string;
  createdAt: string;
  expiresAt: string;
  status: SendRequestStatus;
  acceptedAt?: string;
  declinedAt?: string;
}
