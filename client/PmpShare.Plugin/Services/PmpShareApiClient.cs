using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PmpShare.Plugin.Models;

namespace PmpShare.Plugin.Services;

public sealed class PmpShareApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;

    public PmpShareApiClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<CreateTransferResponse> CreateTransferAsync(
        string baseUrl,
        string testerKey,
        CreateTransferRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "/v1/transfers"))
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        AddTesterKey(message, testerKey);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadJsonOrThrowAsync<CreateTransferResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferMetadata> UploadBlobAsync(
        string baseUrl,
        string testerKey,
        string uploadUrl,
        string encryptedFilePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(encryptedFilePath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = stream.Length;

        using var message = new HttpRequestMessage(HttpMethod.Put, BuildUri(baseUrl, uploadUrl))
        {
            Content = content
        };
        AddTesterKey(message, testerKey);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadJsonOrThrowAsync<TransferMetadata>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferMetadata> GetMetadataAsync(
        string baseUrl,
        string transferId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(BuildUri(baseUrl, $"/v1/transfers/{transferId}/metadata"), cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonOrThrowAsync<TransferMetadata>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadBlobAsync(
        string baseUrl,
        string transferId,
        string encryptedOutputPath,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
                BuildUri(baseUrl, $"/v1/transfers/{transferId}/blob"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(encryptedOutputPath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferMetadata> CompleteTransferAsync(
        string baseUrl,
        string testerKey,
        string transferId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, $"/v1/transfers/{transferId}/complete"));
        AddTesterKey(message, testerKey);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadJsonOrThrowAsync<TransferMetadata>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SendRequest> CreateSendRequestAsync(
        string baseUrl,
        string testerKey,
        CreateSendRequestRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "/v1/send-requests"))
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        AddTesterKey(message, testerKey);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadJsonOrThrowAsync<SendRequest>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SendRequest>> GetInboxAsync(
        string baseUrl,
        string testerKey,
        string recipientId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(baseUrl, $"/v1/inbox?recipientId={Uri.EscapeDataString(recipientId)}"));
        AddTesterKey(message, testerKey);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var inbox = await ReadJsonOrThrowAsync<InboxResponse>(response, cancellationToken).ConfigureAwait(false);
        return inbox.Requests;
    }

    public async Task<SendRequest> AcceptSendRequestAsync(
        string baseUrl,
        string testerKey,
        string requestId,
        CancellationToken cancellationToken)
    {
        return await UpdateSendRequestAsync(baseUrl, testerKey, requestId, "accept", cancellationToken).ConfigureAwait(false);
    }

    public async Task<SendRequest> DeclineSendRequestAsync(
        string baseUrl,
        string testerKey,
        string requestId,
        CancellationToken cancellationToken)
    {
        return await UpdateSendRequestAsync(baseUrl, testerKey, requestId, "decline", cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendRequest> UpdateSendRequestAsync(
        string baseUrl,
        string testerKey,
        string requestId,
        string action,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, $"/v1/send-requests/{requestId}/{action}"));
        AddTesterKey(message, testerKey);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadJsonOrThrowAsync<SendRequest>(response, cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildUri(string baseUrl, string pathOrUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("API base URL must be an absolute URL.");
        }

        return Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(baseUri, pathOrUrl);
    }

    private static void AddTesterKey(HttpRequestMessage message, string testerKey)
    {
        if (!string.IsNullOrWhiteSpace(testerKey))
        {
            message.Headers.TryAddWithoutValidation("X-PmpShare-Key", testerKey);
        }
    }

    private static async Task<T> ReadJsonOrThrowAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException("API returned an empty response.");
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(body, JsonOptions);
            if (envelope?.Error.Message is { Length: > 0 } message)
            {
                throw new InvalidOperationException($"API error {(int)response.StatusCode}: {message}");
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException($"API error {(int)response.StatusCode}: {body}");
    }
}
