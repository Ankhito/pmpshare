import {
  DEFAULT_EXPIRES_IN_SECONDS,
  MAX_ENCRYPTED_SIZE_BYTES,
  MAX_EXPIRES_IN_SECONDS,
} from "./constants";

export interface CreateTransferInput {
  fileName: string;
  plaintextSha256: string;
  encryptedSize: number;
  expiresInSeconds: number;
}

export function validateFileName(fileName: unknown): string {
  if (
    typeof fileName !== "string" ||
    fileName.trim() !== fileName ||
    fileName.length === 0
  ) {
    throw new Error(
      "fileName must be a non-empty string without leading or trailing whitespace.",
    );
  }

  if (!fileName.toLowerCase().endsWith(".pmp")) {
    throw new Error("fileName must end with .pmp.");
  }

  if (
    fileName.includes("/") ||
    fileName.includes("\\") ||
    fileName.includes("..") ||
    /^[a-zA-Z]:/.test(fileName) ||
    fileName.startsWith(".") ||
    fileName.startsWith("~")
  ) {
    throw new Error("fileName must be a simple .pmp file name, not a path.");
  }

  if (!/^[A-Za-z0-9._ ()\[\]-]+\.pmp$/i.test(fileName)) {
    throw new Error("fileName contains unsupported characters.");
  }

  return fileName;
}

export function validateCreateTransferBody(body: unknown): CreateTransferInput {
  if (!body || typeof body !== "object") {
    throw new Error("Request body must be a JSON object.");
  }

  const record = body as Record<string, unknown>;
  const fileName = validateFileName(record.fileName);

  if (
    typeof record.plaintextSha256 !== "string" ||
    !/^[a-fA-F0-9]{64}$/.test(record.plaintextSha256)
  ) {
    throw new Error("plaintextSha256 must be a SHA-256 hex string.");
  }

  if (
    typeof record.encryptedSize !== "number" ||
    !Number.isSafeInteger(record.encryptedSize) ||
    record.encryptedSize <= 0
  ) {
    throw new Error("encryptedSize must be a positive integer.");
  }

  if (record.encryptedSize > MAX_ENCRYPTED_SIZE_BYTES) {
    throw new Error("encryptedSize exceeds the 500 MB MVP limit.");
  }

  let expiresInSeconds = DEFAULT_EXPIRES_IN_SECONDS;
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
      MAX_EXPIRES_IN_SECONDS,
    );
  }

  return {
    fileName,
    plaintextSha256: record.plaintextSha256.toLowerCase(),
    encryptedSize: record.encryptedSize,
    expiresInSeconds,
  };
}
