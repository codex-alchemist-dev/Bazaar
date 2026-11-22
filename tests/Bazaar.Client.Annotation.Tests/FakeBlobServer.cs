using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Client.Annotation.Tests;

/// <summary>
/// An in-memory server for the blob routes, with the same status codes and headers as <c>Bazaar.Server.Http</c>: POST opens,
/// HEAD reports the offset, PATCH appends at <c>Upload-Offset</c> and completes with a hash check. A test can inject a fault
/// on any request. An applied fault changes the server's state and then answers with the fault status, which models a
/// dropped response.
/// </summary>
internal sealed class FakeBlobServer : HttpMessageHandler
{
    private readonly Dictionary<string, Upload> _uploads = [];
    private readonly Dictionary<string, int> _seen = [];

    /// <summary>Stored blobs by hash.</summary>
    public Dictionary<string, byte[]> Blobs { get; } = [];

    /// <summary>Requests seen, as "METHOD path".</summary>
    public List<string> Log { get; } = [];

    /// <summary>
    /// Chooses a fault for a request. Receives the method and the zero-based count of requests with that method so far,
    /// including refused ones. Null means the request is handled normally.
    /// </summary>
    public Func<string, int, Fault?>? Inject { get; set; }

    /// <summary>The number of PATCH requests the server has handled (a refused PATCH is not counted).</summary>
    public int PatchCount { get; private set; }

    /// <summary>Sends one request to the fake, for wrappers in the tests.</summary>
    public Task<HttpResponseMessage> Dispatch(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);

    /// <summary>A replacement answer for a request.</summary>
    /// <param name="Status">The status to answer with.</param>
    /// <param name="Apply">True when the server still handles the request (changes state) before answering with <see cref="Status"/>.</param>
    public sealed record Fault(HttpStatusCode Status, bool Apply = false);

    private sealed class Upload
    {
        public required long Length { get; init; }
        public required string? DeclaredSha { get; init; }
        public List<byte> Bytes { get; } = [];
    }

    /// <summary>The bytes the server has accepted for an upload id, or null when it does not exist.</summary>
    public byte[]? Accepted(string id) => _uploads.TryGetValue(id, out var up) ? up.Bytes.ToArray() : null;

    /// <summary>The upload ids the server currently holds.</summary>
    public IReadOnlyCollection<string> OpenUploads => _uploads.Keys;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var path = request.RequestUri!.AbsolutePath;
        _seen.TryGetValue(method, out var index);
        _seen[method] = index + 1;
        Log.Add($"{method} {path}");

        var fault = Inject?.Invoke(method, index);
        if (fault is { Apply: false }) return new HttpResponseMessage(fault.Status);

        var response = await HandleAsync(request, method, path, cancellationToken);
        if (fault is null) return response;

        response.Dispose();
        return new HttpResponseMessage(fault.Status);
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string method, string path, CancellationToken cancellationToken)
    {
        if (method == "POST" && path == "/blobs")
        {
            var length = long.Parse(request.Headers.GetValues("Upload-Length").Single(), CultureInfo.InvariantCulture);
            var sha = request.Headers.TryGetValues("Upload-Sha256", out var declared) ? declared.Single() : null;
            var id = Guid.NewGuid().ToString("N");
            _uploads[id] = new Upload { Length = length, DeclaredSha = sha };
            return Json(HttpStatusCode.Created, $"{{\"upload_id\":\"{id}\",\"offset\":0,\"length\":{length}}}");
        }

        var id2 = path.StartsWith("/blobs/uploads/", StringComparison.Ordinal) ? path["/blobs/uploads/".Length..] : null;
        if (id2 is null || !_uploads.TryGetValue(id2, out var upload)) return Empty(HttpStatusCode.NotFound);

        if (method == "HEAD")
        {
            var answer = Empty(HttpStatusCode.NoContent);
            answer.Headers.TryAddWithoutValidation("Upload-Offset", upload.Bytes.Count.ToString(CultureInfo.InvariantCulture));
            answer.Headers.TryAddWithoutValidation("Upload-Length", upload.Length.ToString(CultureInfo.InvariantCulture));
            return answer;
        }

        if (method == "PATCH")
        {
            PatchCount++;
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var offset = long.Parse(request.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
            if (offset != upload.Bytes.Count) return Json(HttpStatusCode.Conflict, "{\"error\":\"offset_mismatch\"}");
            if (offset + body.Length > upload.Length) return Json(HttpStatusCode.BadRequest, "{\"error\":\"bad_chunk\"}");

            upload.Bytes.AddRange(body);
            if (upload.Bytes.Count < upload.Length) return Empty(HttpStatusCode.NoContent);

            var hash = Convert.ToHexStringLower(SHA256.HashData(upload.Bytes.ToArray()));
            if (upload.DeclaredSha is not null && !string.Equals(upload.DeclaredSha, hash, StringComparison.Ordinal))
            {
                _uploads.Remove(id2);
                return Json((HttpStatusCode)422, "{\"error\":\"hash_mismatch\"}");
            }

            Blobs[hash] = upload.Bytes.ToArray();
            _uploads.Remove(id2);
            return Json(HttpStatusCode.OK, $"{{\"hash\":\"{hash}\",\"size\":{Blobs[hash].Length}}}");
        }

        return Empty(HttpStatusCode.MethodNotAllowed);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Empty(HttpStatusCode status) => new(status);
}
