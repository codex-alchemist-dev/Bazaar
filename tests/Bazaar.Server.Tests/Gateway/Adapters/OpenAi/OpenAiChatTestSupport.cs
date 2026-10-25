using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.OpenAi;

namespace Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

/// <summary>A request as the stub handler saw it, with its body as text.</summary>
internal sealed record CapturedRequest(Uri Url, string? Authorization, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>An <see cref="HttpMessageHandler"/> that records each request and plays the next queued reply.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _replies = new();

    public List<CapturedRequest> Requests { get; } = [];

    public void Reply(Func<CancellationToken, Task<HttpResponseMessage>> reply) => _replies.Enqueue(reply);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        Requests.Add(new CapturedRequest(request.RequestUri!, request.Headers.Authorization?.ToString(), headers, body));
        return await _replies.Dequeue()(cancellationToken);
    }
}

/// <summary>Canned replies for <see cref="StubHandler"/>.</summary>
internal static class Replies
{
    public static Func<CancellationToken, Task<HttpResponseMessage>> Sse(string body) => _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });

    /// <summary>A stream that delivers <paramref name="body"/> and then fails the way a reset connection does.</summary>
    public static Func<CancellationToken, Task<HttpResponseMessage>> SseThenReset(string body) => _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ResetStream(Encoding.UTF8.GetBytes(body))),
        });

    public static Func<CancellationToken, Task<HttpResponseMessage>> Error(HttpStatusCode status, string body, string? retryAfter = null) => _ =>
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return Task.FromResult(response);
    };

    /// <summary>A server that accepts the request and never answers, so the client timeout fires.</summary>
    public static Func<CancellationToken, Task<HttpResponseMessage>> Hang() => async ct =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    };

    public static Func<CancellationToken, Task<HttpResponseMessage>> Refused() => _ =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("The connection was refused."));
}

/// <summary>Serves its bytes, then throws an <see cref="IOException"/> on the next read.</summary>
internal sealed class ResetStream(byte[] bytes) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= bytes.Length)
        {
            throw new IOException("The connection was reset.");
        }

        var count = Math.Min(buffer.Length, bytes.Length - _position);
        bytes.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class OpenAiChatTestRig
{
    /// <summary>The endpoint used by most tests. The key is a placeholder, not a credential.</summary>
    public static ProviderEndpoint Endpoint { get; } = new("https://provider.example/v1", "requested-model", "test-placeholder-key");

    public static ModelRequest Request { get; } = new(
        "agent-1",
        "route-a",
        [new ModelMessage(ModelRole.User, "Hello")],
        []);

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gateway", "Adapters", "OpenAi", "Fixtures", name));

    public static async Task<List<ModelEvent>> Collect(IAsyncEnumerable<ModelEvent> stream)
    {
        var events = new List<ModelEvent>();
        await foreach (var e in stream)
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>Streams one call through the stub and collects every event.</summary>
    public static async Task<List<ModelEvent>> Run(
        StubHandler handler,
        ProviderEndpoint? endpoint = null,
        ModelRequest? request = null,
        TimeProvider? time = null,
        TimeSpan? timeout = null,
        CancellationToken? ct = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        if (timeout is { } t)
        {
            http.Timeout = t;
        }

        var adapter = new OpenAiChatAdapter(http, time);
        return await Collect(adapter.StreamAsync(endpoint ?? Endpoint, request ?? Request, ct ?? TestContext.Current.CancellationToken));
    }

    public static string TextOf(IEnumerable<ModelEvent> events) =>
        string.Concat(events.OfType<ModelEvent.TextDelta>().Select(e => e.Text));
}
