using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Tests.Wire;

/// <summary>Real sockets against an in-test loopback WebSocket server (HttpListener).</summary>
public sealed class WebSocketWireTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 15_000)]
    public async Task TextMessages_RoundTripBothWays_AsTextFrames()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        await pair.Client.SendAsync("{\"t\":\"ping\"}"u8.ToArray(), Ct);
        var (type, bytes) = await ReceiveMessageAsync(pair.Server, Ct);
        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal("{\"t\":\"ping\"}", System.Text.Encoding.UTF8.GetString(bytes));

        await SendTextAsync(pair.Server, "{\"t\":\"pong\"}");
        WireReceive reply = await pair.Client.ReceiveAsync(Ct);
        Assert.False(reply.IsClosed);
        Assert.Equal("{\"t\":\"pong\"}", System.Text.Encoding.UTF8.GetString(reply.Frame.Span));
    }

    [Fact(Timeout = 15_000)]
    public async Task BinaryFlag_SendsBinaryFrames()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint, binary: true);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        await pair.Client.SendAsync(new byte[] { 1, 2, 3 }, Ct);
        var (type, bytes) = await ReceiveMessageAsync(pair.Server, Ct);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact(Timeout = 15_000)]
    public async Task FragmentedMessage_IsDeliveredAsOneFrame()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        await pair.Server.SendAsync(new byte[] { 1, 2 }, WebSocketMessageType.Text, false, Ct);
        await pair.Server.SendAsync(new byte[] { 3, 4, 5 }, WebSocketMessageType.Text, false, Ct);
        await pair.Server.SendAsync(new byte[] { 6 }, WebSocketMessageType.Text, true, Ct);

        WireReceive frame = await pair.Client.ReceiveAsync(Ct);
        Assert.False(frame.IsClosed);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, frame.Frame.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task MessageExactlyAtLimit_IsDelivered()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint, maxFrameBytes: 1024);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        byte[] payload = Enumerable.Repeat((byte)7, 1024).ToArray();
        await pair.Server.SendAsync(payload, WebSocketMessageType.Binary, true, Ct);

        WireReceive frame = await pair.Client.ReceiveAsync(Ct);
        Assert.False(frame.IsClosed);
        Assert.Equal(payload, frame.Frame.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task MessageOverLimit_ClosesWith4400()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint, maxFrameBytes: 1024);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        // Three fragments of 600 bytes: the first fits, the total (1800) does not.
        byte[] part = Enumerable.Repeat((byte)1, 600).ToArray();
        await pair.Server.SendAsync(part, WebSocketMessageType.Binary, false, Ct);
        await pair.Server.SendAsync(part, WebSocketMessageType.Binary, false, Ct);
        await pair.Server.SendAsync(part, WebSocketMessageType.Binary, true, Ct);

        WireReceive result = await pair.Client.ReceiveAsync(Ct);
        Assert.True(result.IsClosed);
        Assert.Equal(WireCloseCodes.BadFrame, result.CloseCode);

        // The server sees the client's close frame carrying 4400.
        var (type, _) = await ReceiveMessageAsync(pair.Server, Ct);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal((WebSocketCloseStatus)WireCloseCodes.BadFrame, pair.Server.CloseStatus);
    }

    [Fact(Timeout = 15_000)]
    public async Task ServerCloseWithCustomCode_SurfacesAsInt()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        await pair.Server.CloseOutputAsync((WebSocketCloseStatus)WireCloseCodes.UpgradeRequired, "upgrade required", Ct);

        WireReceive result = await pair.Client.ReceiveAsync(Ct);
        Assert.True(result.IsClosed);
        Assert.Equal(4426, result.CloseCode);
    }

    [Fact(Timeout = 15_000)]
    public async Task ServerDroppingConnection_SurfacesAsClosedWithoutCode()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        // Abort then dispose: the TCP connection closes without a close frame.
        pair.Server.Abort();
        pair.Server.Dispose();

        WireReceive result = await pair.Client.ReceiveAsync(Ct);
        Assert.True(result.IsClosed);
        Assert.Equal(0, result.CloseCode);
    }

    [Fact(Timeout = 15_000)]
    public async Task ConnectToClosedPort_Throws()
    {
        var factory = new WebSocketWireTransportFactory(new Uri($"ws://127.0.0.1:{FreePort()}/ws/"));

        await Assert.ThrowsAnyAsync<Exception>(() => factory.ConnectAsync(Ct).AsTask());
    }

    [Fact(Timeout = 15_000)]
    public async Task ConfigureCallback_RunsBeforeConnect_AndSetsHeaders()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint, o => o.SetRequestHeader("Authorization", "Bearer test-token"));

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        Assert.Equal("Bearer test-token", pair.Request.Headers["Authorization"]);
    }

    [Fact(Timeout = 15_000)]
    public async Task ConcurrentSends_DoNotInterleave()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint);

        var acceptTask = server.AcceptAsync(Ct);
        await using var pair = await OpenAsync(factory, acceptTask);

        const int count = 50;
        var reads = ReadMessagesAsync(pair.Server, count, Ct);
        var sends = Enumerable.Range(0, count)
            .Select(i => pair.Client.SendAsync(Enumerable.Repeat((byte)i, 32 * 1024).ToArray(), Ct).AsTask())
            .ToArray();
        await Task.WhenAll(sends);

        byte[][] received = await reads;
        Assert.All(received, message =>
        {
            // Every byte of a message is the same value; interleaved frames would mix values.
            Assert.Equal(32 * 1024, message.Length);
            Assert.All(message, b => Assert.Equal(message[0], b));
        });
        Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), received.Select(m => m[0]).Order());
    }

    [Fact(Timeout = 15_000)]
    public async Task DisposeAsync_SendsNormalClosure()
    {
        using var server = new LoopbackWebSocketServer();
        var factory = new WebSocketWireTransportFactory(server.Endpoint);

        var acceptTask = server.AcceptAsync(Ct);
        var pair = await OpenAsync(factory, acceptTask);

        await pair.Client.DisposeAsync();

        var (type, _) = await ReceiveMessageAsync(pair.Server, Ct);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, pair.Server.CloseStatus);
        pair.Server.Dispose();
    }

    private static async Task<Pair> OpenAsync(WebSocketWireTransportFactory factory, Task<AcceptedSocket> acceptTask)
    {
        IWireConnection client = await factory.ConnectAsync(TestContext.Current.CancellationToken);
        AcceptedSocket accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return new Pair(client, accepted.Socket, accepted.Request);
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one whole message on the server side; a close comes back as <see cref="WebSocketMessageType.Close"/>.</summary>
    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] buffer = new byte[4096];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, []);
            writer.Write(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage) return (result.MessageType, writer.WrittenSpan.ToArray());
        }
    }

    private static async Task<byte[][]> ReadMessagesAsync(WebSocket socket, int count, CancellationToken ct)
    {
        var messages = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            (_, messages[i]) = await ReceiveMessageAsync(socket, ct);
        }

        return messages;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A client connection and the server end of the same socket.</summary>
    private sealed class Pair(IWireConnection client, WebSocket server, HttpListenerRequest request) : IAsyncDisposable
    {
        public IWireConnection Client { get; } = client;

        public WebSocket Server { get; } = server;

        public HttpListenerRequest Request { get; } = request;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Server.Dispose();
        }
    }

    private sealed record AcceptedSocket(HttpListenerRequest Request, WebSocket Socket);

    /// <summary>A one-shot loopback WebSocket server built on <see cref="HttpListener"/>.</summary>
    private sealed class LoopbackWebSocketServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LoopbackWebSocketServer()
        {
            int port = FreePort();
            Endpoint = new Uri($"ws://127.0.0.1:{port}/ws/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/ws/");
            _listener.Start();
        }

        public Uri Endpoint { get; }

        /// <summary>Waits for the next upgrade request and completes the WebSocket handshake.</summary>
        public async Task<AcceptedSocket> AcceptAsync(CancellationToken cancellationToken)
        {
            HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            HttpListenerWebSocketContext ws = await context.AcceptWebSocketAsync(null).WaitAsync(cancellationToken);
            return new AcceptedSocket(context.Request, ws.WebSocket);
        }

        public void Dispose() => _listener.Close();
    }
}
