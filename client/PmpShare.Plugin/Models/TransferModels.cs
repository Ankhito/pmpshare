using System.Text.Json.Serialization;

namespace PmpShare.Plugin.Models;

public sealed record CreateTransferRequest(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("plaintextSha256")] string PlaintextSha256,
    [property: JsonPropertyName("encryptedSize")] long EncryptedSize,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

public sealed record CreateTransferResponse(
    [property: JsonPropertyName("transferId")] string TransferId,
    [property: JsonPropertyName("uploadUrl")] string UploadUrl,
    [property: JsonPropertyName("metadataUrl")] string MetadataUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public sealed record TransferMetadata(
    [property: JsonPropertyName("transferId")] string TransferId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("plaintextSha256")] string PlaintextSha256,
    [property: JsonPropertyName("encryptedSize")] long EncryptedSize,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("downloadCount")] int DownloadCount,
    [property: JsonPropertyName("maxDownloads")] int MaxDownloads,
    [property: JsonPropertyName("blobDeleted")] bool? BlobDeleted = null,
    [property: JsonPropertyName("uploadedAt")] DateTimeOffset? UploadedAt = null,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt = null);

public sealed record ApiErrorEnvelope([property: JsonPropertyName("error")] ApiError Error);

public sealed record ApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
