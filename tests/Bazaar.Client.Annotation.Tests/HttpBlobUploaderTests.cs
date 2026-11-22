using System.Net;
using System.Security.Cryptography;
using Bazaar.Client.Annotation;

namespace Bazaar.Client.Annotation.Tests;

/// <summary>The HTTP uploader against the fake server: chunks, resume, retries and every refusal class.</summary>
public class HttpBlobUploaderTests
{
    private static readonly byte[] _content = Enumerable.Range(0, 10).Select(i => (byte)(i * 7)).ToArray();

    private static readonly string _hash = Convert.ToHexStringLower(SHA256.HashData(_content));

    private static (HttpBlobUploader Uploader, FakeBlobServer Server) Create(AnnotationOptions? options = null)
    {
        var server = new FakeBlobServer();
        var http = new HttpClient(server) { BaseAddress = new Uri("https://blobs.test/") };
        var opts = options ?? new AnnotationOptions(ChunkBytes: 4, RetryDelay: TimeSpan.Zero);
        return (new HttpBlobUploader(http, opts), server);
    }

    private static Task<BlobUploadResult> Upload(HttpBlobUploader uploader, IProgress<BlobUploadProgress>? progress = null) =>
        uploader.UploadAsync(new BlobUploadRequest(_content), progress, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Chunks_are_sent_in_order_and_the_stored_hash_is_returned()
    {
        var (uploader, server) = Create();
        var reports = new List<BlobUploadProgress>();

        var result = await Upload(uploader, new Recorder(reports));

        Assert.Equal(_hash, result.Hash);
        Assert.Equal(_content.Length, result.Size);
        Assert.Equal(_content, server.Blobs[_hash]);
        Assert.Equal(3, server.PatchCount);
        Assert.Equal(new long[] { 0, 4, 8, 10 }, reports.Select(r => r.SentBytes).ToArray());
        Assert.All(reports, r => Assert.Equal(_content.Length, r.TotalBytes));
    }

    [Fact]
    public async Task A_conflict_is_resumed_from_the_offset_the_server_holds()
    {
        var (uploader, server) = Create();
        // The first PATCH is refused with 409, so the server's offset is still zero; the uploader must ask and continue.
        server.Inject = (method, index) => method == "PATCH" && index == 0 ? new FakeBlobServer.Fault(HttpStatusCode.Conflict) : null;

        var result = await Upload(uploader);

        Assert.Equal(_hash, result.Hash);
        Assert.Contains(server.Log, l => l.StartsWith("HEAD /blobs/uploads/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dropped_response_after_an_applied_chunk_is_not_sent_twice()
    {
        var (uploader, server) = Create();
        // The second chunk is stored, but its response is lost (500). The uploader must read the offset and move on.
        server.Inject = (method, index) => method == "PATCH" && index == 1 ? new FakeBlobServer.Fault(HttpStatusCode.InternalServerError, Apply: true) : null;

        var result = await Upload(uploader);

        Assert.Equal(_content, server.Blobs[result.Hash]);
        // Three chunks are handled once each: the one whose response was lost is not sent again.
        Assert.Equal(3, server.PatchCount);
    }

    [Fact]
    public async Task A_transient_status_is_retried_and_the_upload_completes()
    {
        var (uploader, server) = Create();
        server.Inject = (method, index) => method == "PATCH" && index == 0 ? new FakeBlobServer.Fault((HttpStatusCode)503) : null;

        var result = await Upload(uploader);

        Assert.Equal(_hash, result.Hash);
    }

    [Fact]
    public async Task The_upload_gives_up_as_unavailable_after_the_retry_budget()
    {
        var (uploader, server) = Create(new AnnotationOptions(ChunkBytes: 4, MaxAttemptsPerRequest: 2, RetryDelay: TimeSpan.Zero));
        server.Inject = (method, _) => method == "POST" ? new FakeBlobServer.Fault(HttpStatusCode.ServiceUnavailable) : null;

        var error = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(uploader));

        Assert.Equal(AnnotationFailure.Unavailable, error.Failure);
        Assert.Equal(2, server.Log.Count(l => l == "POST /blobs"));
    }

    [Fact]
    public async Task A_hash_refusal_on_a_chunk_is_reported_as_hash_mismatch()
    {
        var (uploader, server) = Create();
        server.Inject = (method, _) => method == "PATCH" ? new FakeBlobServer.Fault((HttpStatusCode)422) : null;

        var error = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(uploader));

        Assert.Equal(AnnotationFailure.HashMismatch, error.Failure);
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public async Task An_open_refused_for_size_is_too_large()
    {
        var (uploader, server) = Create();
        server.Inject = (method, _) => method == "POST" ? new FakeBlobServer.Fault(HttpStatusCode.RequestEntityTooLarge) : null;

        var error = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(uploader));

        Assert.Equal(AnnotationFailure.TooLarge, error.Failure);
    }

    [Fact]
    public async Task An_authorization_refusal_is_rejected_without_retrying()
    {
        var (uploader, server) = Create();
        server.Inject = (method, _) => method == "POST" ? new FakeBlobServer.Fault(HttpStatusCode.Unauthorized) : null;

        var error = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(uploader));

        Assert.Equal(AnnotationFailure.Rejected, error.Failure);
        Assert.Equal(401, error.StatusCode);
        Assert.Single(server.Log);
    }

    [Fact]
    public async Task A_completion_reply_with_the_wrong_hash_is_a_hash_mismatch()
    {
        var wrong = new string('a', 64);
        var reply = CreateWithReply($"{{\"hash\":\"{wrong}\",\"size\":{_content.Length}}}");

        var error = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(reply));

        Assert.Equal(AnnotationFailure.HashMismatch, error.Failure);
    }

    [Fact]
    public async Task An_unreadable_completion_reply_is_a_protocol_error()
    {
        var uploader = CreateWithReply("not json");

        var error = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(uploader));

        Assert.Equal(AnnotationFailure.Protocol, error.Failure);
    }

    /// <summary>An uploader whose completion replies are replaced with <paramref name="completionBody"/>.</summary>
    private static HttpBlobUploader CreateWithReply(string completionBody)
    {
        var server = new FakeBlobServer();
        var http = new HttpClient(new ReplyHandler(server, completionBody)) { BaseAddress = new Uri("https://blobs.test/") };
        return new HttpBlobUploader(http, new AnnotationOptions(ChunkBytes: 4, RetryDelay: TimeSpan.Zero));
    }

    [Fact]
    public async Task An_empty_or_oversized_blob_is_refused_before_any_request()
    {
        var (uploader, server) = Create(new AnnotationOptions(MaxCaptureBytes: 5, RetryDelay: TimeSpan.Zero));

        var tooLarge = await Assert.ThrowsAsync<BlobUploadException>(() => Upload(uploader));
        var empty = await Assert.ThrowsAsync<BlobUploadException>(() =>
            uploader.UploadAsync(new BlobUploadRequest(ReadOnlyMemory<byte>.Empty), null, TestContext.Current.CancellationToken));

        Assert.Equal(AnnotationFailure.TooLarge, tooLarge.Failure);
        Assert.Equal(AnnotationFailure.Rejected, empty.Failure);
        Assert.Empty(server.Log);
    }

    [Fact]
    public async Task Cancelling_before_a_chunk_throws_operation_canceled()
    {
        var (uploader, server) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            uploader.UploadAsync(new BlobUploadRequest(_content), null, cts.Token));

        Assert.Empty(server.Blobs);
        Assert.Equal(0, server.PatchCount);
    }

    /// <summary>Records progress synchronously, so the test sees the reports in the order they were made.</summary>
    private sealed class Recorder(List<BlobUploadProgress> into) : IProgress<BlobUploadProgress>
    {
        public void Report(BlobUploadProgress value) => into.Add(value);
    }

    /// <summary>Serves the fake server's upload, but answers a completing PATCH with a fixed body.</summary>
    private sealed class ReplyHandler(FakeBlobServer inner, string completionBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await inner.Dispatch(request, cancellationToken);
            if (request.Method != HttpMethod.Patch || response.StatusCode != HttpStatusCode.OK) return response;

            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completionBody) };
        }
    }
}
