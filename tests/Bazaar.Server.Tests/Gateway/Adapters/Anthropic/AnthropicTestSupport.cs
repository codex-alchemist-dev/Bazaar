using System.Net;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;

namespace Bazaar.Server.Tests.Gateway.Adapters.Anthropic;

/// <summary>One request as the stub handler saw it. The body is read at send time, so it survives disposal.</summary>
public sealed record SentRequest(Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>An HTTP handler that answers from a delegate and records every request it receives.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<SentRequest> _sent = [];

    public IReadOnlyList<SentRequest> Sent => _sent;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _sent.Add(new SentRequest(request.RequestUri!, headers, body));
        return respond(request);
    }
}

/// <summary>A body stream that delivers a prefix and then fails the way a reset connection does.</summary>
public sealed class ResetAfterStream(byte[] prefix) : Stream
{
    private readonly MemoryStream _inner = new(prefix);

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
        var read = _inner.Read(buffer);
        return read > 0 ? read : throw new IOException("The connection was reset by the peer.");
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public static class AnthropicTestSupport
{
    public const string RequestedModel = "claude-haiku-5-5";

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gateway", "Adapters", "Anthropic", "Fixtures", name));

    public static ProviderEndpoint Endpoint(string? apiKey = "test-key", IReadOnlyDictionary<string, string>? extraHeaders = null) =>
        new("https://api.example.test/", RequestedModel, apiKey, extraHeaders);

    public static ModelRequest SimpleRequest() =>
        new("agent-1", RequestedModel, [new ModelMessage(ModelRole.User, "Hi")], []);

    public static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        }

        return response;
    }

    public static AnthropicMessagesAdapter Adapter(StubHandler handler) =>
        new(new HttpClient(handler, disposeHandler: false));

    public static async Task<List<ModelEvent>> Collect(IModelAdapter adapter, ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct)
    {
        var events = new List<ModelEvent>();
        await foreach (var modelEvent in adapter.StreamAsync(endpoint, request, ct))
        {
            events.Add(modelEvent);
        }

        return events;
    }

    /// <summary>A single SSE event as the wire carries it.</summary>
    public static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    /// <summary>A minimal, complete text-only stream whose <c>message_delta</c> carries the given stop reason.</summary>
    public static string TextStreamWithStopReason(string stopReason) =>
        Event("message_start", """{"type":"message_start","message":{"model":"claude-haiku-5-5-20261001","usage":{"input_tokens":5,"output_tokens":1}}}""")
        + Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
        + Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}""")
        + Event("content_block_stop", """{"type":"content_block_stop","index":0}""")
        + Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":""" + $"\"{stopReason}\"" + ""","stop_sequence":null},"usage":{"output_tokens":2}}""")
        + Event("message_stop", """{"type":"message_stop"}""");
}
