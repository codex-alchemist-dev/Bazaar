using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bazaar.Server.VmEgress.Matching;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>
/// One agent's host proxy (doc 16 WP9, layer 2). Speaks HTTP CONNECT and plain HTTP forwarding on raw sockets. Every request
/// is checked against the agent's allowlist; the proxy resolves names itself, refuses any answer that holds a private, loopback,
/// link-local, multicast or metadata address, and connects to the address it checked, so a second DNS answer cannot redirect
/// the connection (DNS rebinding). <see cref="Stop"/> closes the listener and every open connection at once.
/// </summary>
public sealed class AgentEgressProxy : IAsyncDisposable
{
    private static readonly string[] _hopByHop =
    [
        "connection", "proxy-connection", "proxy-authorization", "proxy-authenticate", "keep-alive", "te", "trailer", "upgrade", "host",
    ];

    private readonly string _agentId;
    private readonly IPAddress _listenAddress;
    private readonly int _requestedPort;
    private readonly VmEgressOptions _options;
    private readonly EgressAuditor _auditor;
    private readonly IReadOnlyList<IProxyRequestFilter> _filters;
    private readonly IHostResolver _resolver;
    private readonly IUpstreamConnector _connector;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, byte> _open = new();
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private TcpListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private Allowlist _allowlist;
    private int _active;
    private int _stopped;

    /// <summary>Creates a proxy for one agent. It listens only once <see cref="Start"/> is called.</summary>
    public AgentEgressProxy(
        string agentId,
        IPAddress listenAddress,
        int port,
        Allowlist allowlist,
        VmEgressOptions options,
        EgressAuditor auditor,
        IReadOnlyList<IProxyRequestFilter>? filters = null)
        : this(agentId, listenAddress, port, allowlist, options, auditor, filters, new SystemHostResolver(), new SocketUpstreamConnector())
    {
    }

    internal AgentEgressProxy(
        string agentId,
        IPAddress listenAddress,
        int port,
        Allowlist allowlist,
        VmEgressOptions options,
        EgressAuditor auditor,
        IReadOnlyList<IProxyRequestFilter>? filters,
        IHostResolver resolver,
        IUpstreamConnector connector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        options.Validate();
        _agentId = agentId;
        _listenAddress = listenAddress ?? throw new ArgumentNullException(nameof(listenAddress));
        _requestedPort = port;
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _options = options;
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _filters = filters ?? [];
        _resolver = resolver;
        _connector = connector;
    }

    /// <summary>The bound endpoint, once started.</summary>
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>True once <see cref="Stop"/> has run. A stopped proxy cannot be restarted; make a new one.</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>Connections currently being served.</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>Starts listening and returns the bound endpoint.</summary>
    public IPEndPoint Start()
    {
        ObjectDisposedException.ThrowIf(IsStopped, this);
        if (_listener is not null)
        {
            return LocalEndPoint!;
        }

        var listener = new TcpListener(_listenAddress, _requestedPort);
        listener.Start(backlog: Math.Max(16, _options.MaxConnectionsPerAgent));
        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
        return LocalEndPoint!;
    }

    /// <summary>Replaces the allowlist. New requests see it at once; tunnels already open are not interrupted by a change.</summary>
    public void UpdateAllowlist(Allowlist allowlist) => Volatile.Write(ref _allowlist, allowlist ?? throw new ArgumentNullException(nameof(allowlist)));

    /// <summary>
    /// Closes the listener and every open connection, client and upstream side, right now. Idempotent. This is the kill-switch path,
    /// so it does not wait for in-flight work and never throws.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        try
        {
            _stop.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation callbacks threw; the sockets below are closed regardless.
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        foreach (var socket in _open.Keys)
        {
            CloseNow(socket);
        }
    }

    /// <summary>Stops, then waits briefly for handlers to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await Task.WhenAll([_acceptLoop, .. _handlers.Keys]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return;
            }

            if (Interlocked.Increment(ref _active) > _options.MaxConnectionsPerAgent || !Track(client))
            {
                Interlocked.Decrement(ref _active);
                _open.TryRemove(client, out _);
                client.Dispose();
                continue;
            }

            var handler = HandleAsync(client);
            _handlers[handler] = 0;
            _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(Socket client)
    {
        try
        {
            await Task.Yield();
            await ServeAsync(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // The peer went away, a timeout fired, or Stop closed the socket. All of these just end the connection.
        }
        finally
        {
            Release(client);
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task ServeAsync(Socket client)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.MaxHeaderBytes);
        try
        {
            var filled = 0;
            var end = -1;
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                headerCts.CancelAfter(_options.HeaderTimeout);
                while (end < 0)
                {
                    if (filled == _options.MaxHeaderBytes)
                    {
                        await RespondAsync(client, 431, "Request Header Fields Too Large").ConfigureAwait(false);
                        return;
                    }

                    var n = await client.ReceiveAsync(buffer.AsMemory(filled, _options.MaxHeaderBytes - filled), SocketFlags.None, headerCts.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        return;
                    }

                    filled += n;
                    end = RequestHead.FindEnd(buffer.AsSpan(0, filled));
                }
            }

            var error = RequestHead.TryParse(buffer.AsSpan(0, filled), end, _options.MaxHeaderCount, out var head);
            if (error != HeadError.None)
            {
                await RespondAsync(client, error == HeadError.TooManyHeaders ? 431 : 400, error == HeadError.TooManyHeaders ? "Request Header Fields Too Large" : "Bad Request").ConfigureAwait(false);
                return;
            }

            var leftover = buffer.AsMemory(end, filled - end);
            if (string.Equals(head.Method, "CONNECT", StringComparison.Ordinal))
            {
                await ServeConnectAsync(client, head, leftover).ConfigureAwait(false);
            }
            else
            {
                await ServePlainAsync(client, head, leftover).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ServeConnectAsync(Socket client, RequestHead head, ReadOnlyMemory<byte> leftover)
    {
        if (!RequestTarget.TryParseAuthority(head.Target, 443, out var host, out var port))
        {
            await DenyAsync(client, 400, "Bad Request", Trim(head.Target), 0, "bad-target").ConfigureAwait(false);
            return;
        }

        var upstream = await AdmitAsync(client, host, port).ConfigureAwait(false);
        if (upstream is null)
        {
            return;
        }

        try
        {
            await _auditor.AllowAsync(_agentId, host.Name, port).ConfigureAwait(false);
            await SendAllAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _stop.Token).ConfigureAwait(false);
            if (!leftover.IsEmpty)
            {
                await SendAllAsync(upstream, leftover, _stop.Token).ConfigureAwait(false);
            }

            await RelayAsync(client, upstream, closeAllOnUpstreamEof: false).ConfigureAwait(false);
        }
        finally
        {
            Release(upstream);
        }
    }

    private async Task ServePlainAsync(Socket client, RequestHead head, ReadOnlyMemory<byte> leftover)
    {
        if (!RequestTarget.TryParseAbsoluteHttp(head.Target, out var host, out var port, out var path))
        {
            // Origin-form requests are not proxy requests; https must use CONNECT.
            await DenyAsync(client, 400, "Bad Request", Trim(head.Target), 0, "not-a-proxy-request").ConfigureAwait(false);
            return;
        }

        var lengths = head.Headers.Where(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).Distinct(StringComparer.Ordinal).ToList();
        var hasTransferEncoding = head.Headers.Any(h => h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase));
        if (lengths.Count > 1 || (lengths.Count == 1 && hasTransferEncoding) || lengths.Any(l => l.Length == 0 || l.Any(c => !char.IsAsciiDigit(c))))
        {
            await DenyAsync(client, 400, "Bad Request", host.Name, port, "ambiguous-body-framing").ConfigureAwait(false);
            return;
        }

        var upstream = await AdmitAsync(client, host, port).ConfigureAwait(false);
        if (upstream is null)
        {
            return;
        }

        try
        {
            await ForwardPlainAsync(client, upstream, head, host, port, path, leftover).ConfigureAwait(false);
        }
        finally
        {
            Release(upstream);
        }
    }

    private async Task ForwardPlainAsync(Socket client, Socket upstream, RequestHead head, HostName host, int port, string path, ReadOnlyMemory<byte> leftover)
    {
        var headers = head.Headers.Where(h => !_hopByHop.Contains(h.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        var context = new ProxyRequestContext(_agentId, host.Name, port, head.Method, path, headers);
        foreach (var filter in _filters)
        {
            await filter.FilterAsync(context, _stop.Token).ConfigureAwait(false);
        }

        if (context.Rejected)
        {
            await DenyAsync(client, 403, "Forbidden", host.Name, port, "filter-rejected").ConfigureAwait(false);
            return;
        }

        var authority = (host.Address?.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + host.Name + "]" : host.Name)
            + (port == 80 ? "" : ":" + port);
        var text = new StringBuilder()
            .Append(head.Method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: ").Append(authority).Append("\r\n");
        foreach (var (name, value) in context.Headers)
        {
            if (!_hopByHop.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                text.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        text.Append("Connection: close\r\n\r\n");
        await _auditor.AllowAsync(_agentId, host.Name, port).ConfigureAwait(false);
        await SendAllAsync(upstream, Encoding.UTF8.GetBytes(text.ToString()), _stop.Token).ConfigureAwait(false);
        if (!leftover.IsEmpty)
        {
            await SendAllAsync(upstream, leftover, _stop.Token).ConfigureAwait(false);
        }

        await RelayAsync(client, upstream, closeAllOnUpstreamEof: true).ConfigureAwait(false);
    }

    /// <summary>
    /// The checks every request passes: allowlist, then resolution, then the address refusals on every answer, then a connect to
    /// the pinned address. Sends the error response and returns null when any step fails.
    /// </summary>
    private async Task<Socket?> AdmitAsync(Socket client, HostName host, int port)
    {
        if (!Volatile.Read(ref _allowlist).IsAllowed(host, port))
        {
            await DenyAsync(client, 403, "Forbidden", host.Name, port, "not-allowlisted").ConfigureAwait(false);
            return null;
        }

        IReadOnlyList<IPAddress> addresses;
        if (host.Address is { } literal)
        {
            addresses = [literal];
        }
        else
        {
            using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            resolveCts.CancelAfter(_options.ConnectTimeout);
            try
            {
                addresses = await _resolver.ResolveAsync(host.Name, resolveCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
                addresses = [];
            }
        }

        if (addresses.Count == 0)
        {
            await DenyAsync(client, 502, "Bad Gateway", host.Name, port, "resolve-failed").ConfigureAwait(false);
            return null;
        }

        // Any forbidden answer refuses the whole name. A mixed answer is how rebinding and split-horizon tricks start.
        if (addresses.Any(AddressGuard.IsForbidden))
        {
            await DenyAsync(client, 403, "Forbidden", host.Name, port, "forbidden-address").ConfigureAwait(false);
            return null;
        }

        foreach (var address in addresses)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connectCts.CancelAfter(_options.ConnectTimeout);
            try
            {
                var upstream = await _connector.ConnectAsync(new IPEndPoint(address, port), connectCts.Token).ConfigureAwait(false);
                if (!Track(upstream))
                {
                    CloseNow(upstream);
                    return null;
                }

                return upstream;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !_stop.IsCancellationRequested)
            {
                // Try the next address from the same, already checked, answer.
            }
        }

        await DenyAsync(client, 502, "Bad Gateway", host.Name, port, "connect-failed").ConfigureAwait(false);
        return null;
    }

    private async Task DenyAsync(Socket client, int status, string reason, string host, int port, string why)
    {
        await _auditor.DenyAsync(_agentId, host, port, why).ConfigureAwait(false);
        await RespondAsync(client, status, reason).ConfigureAwait(false);
    }

    private static async Task RespondAsync(Socket client, int status, string reason)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await SendAllAsync(client, bytes, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // The client is gone; nothing more to tell it.
        }
    }

    private async Task RelayAsync(Socket client, Socket upstream, bool closeAllOnUpstreamEof)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var idleMs = (long)_options.IdleTimeout.TotalMilliseconds;
        var last = Environment.TickCount64;

        async Task Pump(Socket from, Socket to, bool fromUpstream)
        {
            var buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    var n = await from.ReceiveAsync(buf.AsMemory(), SocketFlags.None, cts.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    Volatile.Write(ref last, Environment.TickCount64);
                    await SendAllAsync(to, buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                    Volatile.Write(ref last, Environment.TickCount64);
                }

                if (fromUpstream && closeAllOnUpstreamEof)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        to.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                    }
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        async Task Watchdog()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(10, idleMs / 4)), cts.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - Volatile.Read(ref last) > idleMs)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        var watchdog = Watchdog();
        await Task.WhenAll(Pump(client, upstream, false), Pump(upstream, client, true)).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await watchdog.ConfigureAwait(false);
        CloseNow(upstream);
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            var sent = await socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            data = data[sent..];
        }
    }

    private bool Track(Socket socket)
    {
        _open[socket] = 0;
        if (!IsStopped)
        {
            return true;
        }

        // Stop ran between the accept and now; make sure this socket is closed too.
        CloseNow(socket);
        return false;
    }

    private void Release(Socket socket)
    {
        _open.TryRemove(socket, out _);
        CloseNow(socket);
    }

    private static void CloseNow(Socket socket)
    {
        try
        {
            socket.Close(0);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200];

    private static bool IsExpected(Exception ex) =>
        ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException;
}
