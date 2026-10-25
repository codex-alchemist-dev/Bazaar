using System.Buffers;
using System.Net.WebSockets;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Server.Tests.Wire.Harness;

/// <summary>A scripted client on <see cref="ClientWebSocket"/>.</summary>
public sealed class WireTestClient : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    /// <summary>The JSON codec used for every frame.</summary>
    public IWireCodec Codec { get; } = new JsonWireCodec();

    /// <summary>The close code the server sent, once the socket closed.</summary>
    public int? CloseCode => _socket.CloseStatus is { } s ? (int)s : null;

    /// <summary>Opens the socket, optionally with an <c>Authorization: Bearer</c> header.</summary>
    public async Task ConnectAsync(Uri uri, string? bearer = null)
    {
        if (bearer is not null) _socket.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        await _socket.ConnectAsync(uri, cts.Token);
    }

    /// <summary>Builds a default hello.</summary>
    public static Hello DefaultHello(string deviceId = "device-1", string? credential = null, int min = 1, int max = 1, IReadOnlyList<string>? codecs = null) =>
        new(min, max, 0, codecs ?? ["json"], deviceId, "test", "0.0.1", credential, null);

    /// <summary>Sends hello and returns the answer.</summary>
    public async Task<Envelope> HelloAsync(Hello hello)
    {
        await SendAsync(RequestOf(WireTypes.Hello, hello));
        return await ReceiveAsync();
    }

    /// <summary>A request envelope with the next id (or <paramref name="id"/>).</summary>
    public Envelope RequestOf<T>(string type, T payload, string? id = null) =>
        new(1, type, EnvelopeKind.Request, id ?? $"r{Interlocked.Increment(ref _nextId)}", null, null, null, null, Codec.EncodePayload(payload));

    /// <summary>Sends an envelope.</summary>
    public async Task SendAsync(Envelope envelope)
    {
        var writer = new ArrayBufferWriter<byte>();
        Codec.Encode(envelope, writer);
        await SendRawAsync(writer.WrittenMemory, WebSocketMessageType.Text);
    }

    /// <summary>Sends raw bytes as one message.</summary>
    public async Task SendRawAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type)
    {
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        await _socket.SendAsync(bytes, type, true, cts.Token);
    }

    /// <summary>Reads the next frame; fails the test when the server closes instead.</summary>
    public async Task<Envelope> ReceiveAsync()
    {
        var frame = await TryReceiveAsync();
        return frame ?? throw new InvalidOperationException($"Socket closed with {CloseCode} instead of a frame.");
    }

    /// <summary>Reads the next frame, or null when the server closed the socket.</summary>
    public async Task<Envelope?> TryReceiveAsync()
    {
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await _socket.ReceiveAsync(writer.GetMemory(4096), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Finish the close handshake the way a real client does.
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", cts.Token);
                }
                catch (WebSocketException)
                {
                }

                return null;
            }

            writer.Advance(result.Count);
            if (result.EndOfMessage) return Codec.Decode(writer.WrittenSpan);
        }
    }

    /// <summary>Reads frames until the server closes the socket and returns the close code.</summary>
    public async Task<int> WaitForCloseAsync()
    {
        while (await TryReceiveAsync() is not null)
        {
        }

        return CloseCode ?? throw new InvalidOperationException("Closed without a status.");
    }

    /// <summary>Opens a socket and completes the handshake with the credential in the header.</summary>
    public static async Task<WireTestClient> ConnectedAsync(WireTestServer server, string deviceId = "device-1", string credential = "good")
    {
        var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, credential);
        var welcome = await client.HelloAsync(DefaultHello(deviceId));
        if (welcome.T != WireTypes.Welcome) throw new InvalidOperationException($"Expected welcome, got {welcome.T}.");
        return client;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
