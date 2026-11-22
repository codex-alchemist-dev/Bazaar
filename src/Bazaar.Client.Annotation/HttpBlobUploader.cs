using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bazaar.Client.Annotation;

/// <summary>
/// Uploads a blob over the server's resumable HTTP blob surface (doc 02; routes in <c>Bazaar.Server</c>): <c>POST /blobs</c>
/// opens an upload, <c>PATCH /blobs/uploads/{id}</c> appends a chunk at <c>Upload-Offset</c>, and <c>HEAD</c> reports the
/// offset the server holds. The head supplies the <see cref="HttpClient"/> with its base address and the device credential
/// (Bearer), so this class holds no credential. After a dropped chunk or a 409, the uploader asks the server for its
/// offset and continues from there, so accepted bytes are never sent twice.
/// </summary>
public sealed class HttpBlobUploader : IBlobUploader
{
    private const string _uploadLength = "Upload-Length";
    private const string _uploadSha256 = "Upload-Sha256";
    private const string _uploadOffset = "Upload-Offset";
    private const string _uploadIdProperty = "upload_id";

    private readonly HttpClient _http;
    private readonly AnnotationOptions _options;

    /// <param name="http">Client with its base address set to the server origin and the device credential attached.</param>
    /// <param name="options">Chunk size and retry limits.</param>
    public HttpBlobUploader(HttpClient http, AnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<BlobUploadResult> UploadAsync(BlobUploadRequest request, IProgress<BlobUploadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = request.Content;
        long total = content.Length;
        if (total < 1) throw new BlobUploadException(AnnotationFailure.Rejected, "The blob is empty.");
        if (total > _options.MaxCaptureBytes) throw new BlobUploadException(AnnotationFailure.TooLarge, "The blob is too large.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var id = await OpenAsync(total, hash, cancellationToken);
        progress?.Report(new BlobUploadProgress(0, total));

        long offset = 0;
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(_options.ChunkBytes, total - offset);

            using var message = new HttpRequestMessage(HttpMethod.Patch, $"blobs/uploads/{id}");
            message.Headers.TryAddWithoutValidation(_uploadOffset, offset.ToString(CultureInfo.InvariantCulture));
            message.Content = new ByteArrayContent(content.Slice((int)offset, length).ToArray());

            using var response = await TrySendAsync(message, cancellationToken);
            if (response is { StatusCode: HttpStatusCode.NoContent })
            {
                offset += length;
                failures = 0;
                progress?.Report(new BlobUploadProgress(offset, total));
                if (offset == total) throw new BlobUploadException(AnnotationFailure.Protocol, "The server did not return the stored blob.");
                continue;
            }

            if (response is { StatusCode: HttpStatusCode.OK })
            {
                var result = await CompletionAsync(response, hash, total, offset + length, cancellationToken);
                progress?.Report(new BlobUploadProgress(total, total));
                return result;
            }

            if (response is not null && !IsRetryable(response.StatusCode) && response.StatusCode != HttpStatusCode.Conflict)
            {
                throw Refusal(response.StatusCode, "upload");
            }

            // A conflict, a transient status or a dropped connection: ask the server where it is, then resume from there.
            failures = await BackOffAsync(failures, cancellationToken);
            offset = await ReadOffsetAsync(id, offset, total, cancellationToken);
        }
    }

    /// <summary>Opens an upload and returns its id. Retries transient failures.</summary>
    private async Task<string> OpenAsync(long total, string hash, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "blobs");
            message.Headers.TryAddWithoutValidation(_uploadLength, total.ToString(CultureInfo.InvariantCulture));
            message.Headers.TryAddWithoutValidation(_uploadSha256, hash);

            using var response = await TrySendAsync(message, cancellationToken);
            if (response is { StatusCode: HttpStatusCode.Created })
            {
                return await ReadUploadIdAsync(response, cancellationToken);
            }

            if (response is not null && !IsRetryable(response.StatusCode))
            {
                throw Refusal(response.StatusCode, "upload");
            }
            failures = await BackOffAsync(failures, cancellationToken);
        }
    }

    /// <summary>Pauses before a retry, or throws <see cref="AnnotationFailure.Unavailable"/> when the budget is spent.</summary>
    private async Task<int> BackOffAsync(int failures, CancellationToken cancellationToken)
    {
        var next = failures + 1;
        if (next >= _options.MaxAttemptsPerRequest)
        {
            throw new BlobUploadException(AnnotationFailure.Unavailable, "The server did not accept the upload after several tries.");
        }

        // Doubles on each further retry: the delay for the first retry, then twice that, and so on.
        var delay = _options.EffectiveRetryDelay * Math.Pow(2, failures);
        await Task.Delay(delay, cancellationToken);
        return next;
    }

    /// <summary>Asks the server for the offset it holds. Keeps the local offset when the server cannot be asked.</summary>
    private async Task<long> ReadOffsetAsync(string id, long local, long total, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Head, $"blobs/uploads/{id}");
        using var response = await TrySendAsync(message, cancellationToken);
        if (response is null || IsRetryable(response.StatusCode)) return local;
        if (response.StatusCode != HttpStatusCode.NoContent) throw Refusal(response.StatusCode, "upload status");

        if (!response.Headers.TryGetValues(_uploadOffset, out var values)
            || !long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0 || offset >= total)
        {
            throw new BlobUploadException(AnnotationFailure.Protocol, "The server reported an invalid upload offset.");
        }
        return offset;
    }

    /// <summary>Checks the completion body: the stored hash and size must match what was sent.</summary>
    private static async Task<BlobUploadResult> CompletionAsync(HttpResponseMessage response, string hash, long total, long sentToEnd, CancellationToken cancellationToken)
    {
        if (sentToEnd != total) throw new BlobUploadException(AnnotationFailure.Protocol, "The server completed the upload early.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var stored = doc.RootElement.GetProperty("hash").GetString();
            var size = doc.RootElement.GetProperty("size").GetInt64();
            if (!string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase) || size != total)
            {
                throw new BlobUploadException(AnnotationFailure.HashMismatch, "The stored blob does not match the bytes sent.", 200);
            }
            return new BlobUploadResult(hash, size);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BlobUploadException(AnnotationFailure.Protocol, "The server's completion reply was not understood.", 200);
        }
    }

    private static async Task<string> ReadUploadIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty(_uploadIdProperty).GetString();
            if (id is { Length: 32 } && id.All(char.IsAsciiHexDigit)) return id.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Falls through to the protocol error below.
        }
        throw new BlobUploadException(AnnotationFailure.Protocol, "The server's open reply was not understood.", 201);
    }

    /// <summary>Sends one request. Returns null when the network failed or the HTTP client timed out; the caller decides whether to retry.</summary>
    private async Task<HttpResponseMessage?> TrySendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the user's cancel.
            return null;
        }
    }

    private static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static BlobUploadException Refusal(HttpStatusCode status, string context) => status switch
    {
        HttpStatusCode.RequestEntityTooLarge => new BlobUploadException(AnnotationFailure.TooLarge, "The server rejected the size of the " + context + ".", 413),
        (HttpStatusCode)422 => new BlobUploadException(AnnotationFailure.HashMismatch, "The server rejected the hash of the upload.", 422),
        _ => new BlobUploadException(AnnotationFailure.Rejected, "The server refused the " + context + ".", (int)status),
    };
}
