using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Bazaar.Conduit.Relay;

namespace Bazaar.Conduit.Tests.Relay;

/// <summary>The relay is an untrusted peer: real sockets against an in-test loopback server (HttpListener).</summary>
public sealed class ClientWebSocketRelaySocketTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task A_relay_that_streams_an_endless_message_is_cut_off_instead_of_buffered_without_limit()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/relay/");
        listener.Start();

        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var fragment = new byte[16 * 1024];
            try
            {
                // 4,000 fragments of 16 KiB (about 64 MiB) in one message, then the end of it.
                for (int i = 0; i < 4_000; i++)
                {
                    await ws.SendAsync(fragment, WebSocketMessageType.Text, endOfMessage: false, Ct);
                }

                await ws.SendAsync(fragment, WebSocketMessageType.Text, endOfMessage: true, Ct);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The client cut the connection: expected.
            }
        }, Ct);

        var socket = new ClientWebSocketRelaySocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/relay/"), Ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => socket.ReceiveAsync(Ct));

        socket.Dispose(); // the module does this after a failed drain; it also unblocks the server's writes
        listener.Close();
        await serving.WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
