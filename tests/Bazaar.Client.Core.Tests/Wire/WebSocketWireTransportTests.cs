using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Tests.Wire;

/// <summary>Real sockets against an in-test loopback WebSocket server (TcpListener with a hand-written upgrade).</summary>
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

        // Send and receive concurrently: the client must read while the server is still writing fragments,
        // so the test never depends on the server's writes completing before the client starts reading.
        Task sending = SendFragmentsAsync(pair.Server, Ct);

        WireReceive frame = await pair.Client.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await sending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

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

        // Each step is bounded, so a stall fails at the step that stalled instead of running into the test timeout.
        await pair.Server.CloseOutputAsync((WebSocketCloseStatus)WireCloseCodes.UpgradeRequired, "upgrade required", Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        WireReceive result = await pair.Client.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
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

    private static async Task SendFragmentsAsync(WebSocket socket, CancellationToken ct)
    {
        await socket.SendAsync(new byte[] { 1, 2 }, WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(new byte[] { 3, 4, 5 }, WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(new byte[] { 6 }, WebSocketMessageType.Text, true, ct);
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
    private sealed class Pair(IWireConnection client, WebSocket server, AcceptedRequest request) : IAsyncDisposable
    {
        public IWireConnection Client { get; } = client;

        public WebSocket Server { get; } = server;

        public AcceptedRequest Request { get; } = request;

        public async ValueTask DisposeAsync()
        {
            // The client's close waits for the server's closing frame. Answer it concurrently, so teardown takes
            // milliseconds instead of running into the client's close timeout.
            Task answered = AnswerCloseAsync();
            await Client.DisposeAsync();
            await answered.WaitAsync(TimeSpan.FromSeconds(10));
            Server.Dispose();
        }

        private async Task AnswerCloseAsync()
        {
            try
            {
                byte[] buffer = new byte[4096];
                while (Server.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await Server.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }

                if (Server.State is WebSocketState.CloseReceived)
                {
                    await Server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                // The connection is already gone or the test closed the server end itself; nothing is left to answer.
            }
        }
    }

    private sealed record AcceptedRequest(IReadOnlyDictionary<string, string> Headers);

    private sealed record AcceptedSocket(AcceptedRequest Request, WebSocket Socket);

    /// <summary>
    /// A one-shot loopback WebSocket server on a plain <see cref="TcpListener"/> bound to port 0, so the operating system
    /// hands out the port with no find-then-bind race and no http.sys URL registration shared with other processes.
    /// It performs the RFC 6455 upgrade by hand and then wraps the stream as the server end of a WebSocket.
    /// </summary>
    private sealed class LoopbackWebSocketServer : IDisposable
    {
        private const string _handshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private readonly TcpListener _listener;

        public LoopbackWebSocketServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/ws/");
        }

        public Uri Endpoint { get; }

        /// <summary>Waits for the next upgrade request and completes the WebSocket handshake.</summary>
        [SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "RFC 6455 section 4.2.2 requires SHA-1 for Sec-WebSocket-Accept; it is not a security control.")]
        public async Task<AcceptedSocket> AcceptAsync(CancellationToken cancellationToken)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            NetworkStream stream = client.GetStream();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (await ReadHeadAsync(stream, cancellationToken)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            string accept = Convert.ToBase64String(SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + _handshakeGuid)));
            byte[] response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(response, cancellationToken);

            WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            return new AcceptedSocket(new AcceptedRequest(headers), socket);
        }

        public void Dispose() => _listener.Stop();

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            // Read byte by byte up to the blank line so nothing past the HTTP head is consumed from the WebSocket stream.
            var head = new List<byte>();
            byte[] one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, cancellationToken) == 0)
                {
                    throw new IOException("The client closed the connection during the handshake.");
                }

                head.Add(one[0]);
                int n = head.Count;
                if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                {
                    return System.Text.Encoding.ASCII.GetString(head.ToArray());
                }
            }
        }
    }
}
