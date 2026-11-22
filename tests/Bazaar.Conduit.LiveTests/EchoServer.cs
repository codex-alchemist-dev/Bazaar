using System.Net;
using System.Net.WebSockets;

namespace Bazaar.Conduit.LiveTests;

/// <summary>A loopback WebSocket echo server at <c>/ws</c>, standing in for the Bazaar server listener behind a tunnel.</summary>
public sealed class EchoServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <summary>Starts listening on a free loopback port.</summary>
    public EchoServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    /// <summary>The loopback port, for the tunnel's local side.</summary>
    public int Port { get; }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        if (!context.Request.IsWebSocketRequest || context.Request.Url?.AbsolutePath != "/ws")
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        try
        {
            var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            var socket = accepted.WebSocket;
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // The peer went away or the test is over; nothing to report for an echo server.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
