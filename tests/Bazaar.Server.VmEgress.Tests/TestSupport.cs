using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmEgress.Matching;
using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress.Tests;

internal sealed class CapturingAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public IReadOnlyList<AuditEntry> Entries => [.. _entries];

    public IEnumerable<AuditEntry> OfKind(string kind) => Entries.Where(e => e.Kind == kind);

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        _entries.Enqueue(e);
        return Task.CompletedTask;
    }
}

/// <summary>Name to address answers, one list per call so a test can script a rebinding. Counts lookups.</summary>
internal sealed class ScriptedResolver : IHostResolver
{
    private readonly ConcurrentDictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.Ordinal);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<string> Asked { get; } = new();

    /// <summary>Queues answers; the last one repeats.</summary>
    public ScriptedResolver Answer(string host, params string[][] answers)
    {
        _answers[host] = new Queue<IPAddress[]>(answers.Select(a => a.Select(IPAddress.Parse).ToArray()));
        return this;
    }

    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Asked.Enqueue(host);
        if (!_answers.TryGetValue(host, out var queue))
        {
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>([]);
        }

        lock (queue)
        {
            var next = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(next);
        }
    }
}

/// <summary>Records the endpoint the proxy asked for, then connects to the local fake upstream instead.</summary>
internal sealed class RedirectingConnector(IPEndPoint fake) : IUpstreamConnector
{
    public ConcurrentQueue<IPEndPoint> Requested { get; } = new();

    public async ValueTask<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        Requested.Enqueue(endpoint);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(fake, ct);
        return socket;
    }
}

/// <summary>A local upstream. HTTP mode answers one request then closes; echo mode returns what it is sent.</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _echo;
    private readonly Task _loop;
    private int _connections;
    private int _closedByPeer;

    public TimeSpan DrainFor { get; init; } = TimeSpan.FromMilliseconds(300);

    public FakeUpstream(bool echo)
    {
        _echo = echo;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Connections the upstream saw end because the proxy closed them.</summary>
    public int ClosedByPeer => Volatile.Read(ref _closedByPeer);

    public ConcurrentQueue<string> Heads { get; } = new();

    private readonly StringBuilder _all = new();

    /// <summary>Every byte the HTTP-mode upstream received, as text, including any after the request.</summary>
    public string AllBytes
    {
        get
        {
            lock (_all)
            {
                return _all.ToString();
            }
        }
    }

    private void Record(string chunk)
    {
        lock (_all)
        {
            _all.Append(chunk);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => ServeAsync(socket));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        using (socket)
        {
            var buffer = new byte[8192];
            try
            {
                if (_echo)
                {
                    int n;
                    while ((n = await socket.ReceiveAsync(buffer, _cts.Token)) > 0)
                    {
                        await socket.SendAsync(buffer.AsMemory(0, n), _cts.Token);
                    }

                    Interlocked.Increment(ref _closedByPeer);
                    return;
                }

                var text = new StringBuilder();
                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await socket.ReceiveAsync(buffer, _cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    Record(Encoding.UTF8.GetString(buffer, 0, n));
                }

                Heads.Enqueue(text.ToString());
                await socket.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), _cts.Token);

                // Keep listening briefly so a request pipelined behind this one would show up in AllBytes.
                using var drain = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                drain.CancelAfter(DrainFor);
                try
                {
                    int m;
                    while ((m = await socket.ReceiveAsync(buffer, drain.Token)) > 0)
                    {
                        Record(Encoding.UTF8.GetString(buffer, 0, m));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }
            catch (SocketException) when (_echo)
            {
                // The proxy closes with a reset, which is how an abrupt close reaches this side.
                Interlocked.Increment(ref _closedByPeer);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>A raw client for the proxy.</summary>
internal sealed class RawClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;

    public static async Task<RawClient> ConnectAsync(IPEndPoint endpoint)
    {
        var client = new RawClient();
        await client._tcp.ConnectAsync(endpoint);
        client._stream = client._tcp.GetStream();
        return client;
    }

    public Task SendAsync(string text) => _stream!.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    public Task SendBytesAsync(byte[] bytes) => _stream!.WriteAsync(bytes).AsTask();

    /// <summary>Reads exactly <paramref name="count"/> bytes, or fewer if the peer closes or <paramref name="timeout"/> passes.</summary>
    public async Task<byte[]> ReadExactAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[count];
        var got = 0;
        try
        {
            while (got < count)
            {
                var n = await _stream!.ReadAsync(buffer.AsMemory(got), cts.Token);
                if (n == 0)
                {
                    break;
                }

                got += n;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }

        return buffer[..got];
    }

    /// <summary>Reads until the peer closes or <paramref name="timeout"/> passes. Returns what arrived and whether the peer closed.</summary>
    public async Task<(string Text, bool Closed)> ReadToEndAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        var text = new StringBuilder();
        try
        {
            int n;
            while ((n = await _stream!.ReadAsync(buffer, cts.Token)) > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }

            return (text.ToString(), true);
        }
        catch (OperationCanceledException)
        {
            return (text.ToString(), false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return (text.ToString(), true);
        }
    }

    /// <summary>Reads until the text holds <paramref name="marker"/>.</summary>
    public async Task<string> ReadUntilAsync(string marker, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        var text = new StringBuilder();
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var n = await _stream!.ReadAsync(buffer, cts.Token);
            if (n == 0)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }

        return text.ToString();
    }

    public void Dispose() => _tcp.Dispose();
}

/// <summary>A scripted set of "this machine's" addresses, so tests do not depend on the real interfaces.</summary>
internal sealed class FixedLocalAddresses(params string[] addresses) : ILocalAddressSource
{
    private readonly HashSet<IPAddress> _set = [.. addresses.Select(a => Canonical(IPAddress.Parse(a)))];

    public bool IsLocal(IPAddress address) => _set.Contains(Canonical(address));

    private static IPAddress Canonical(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

/// <summary>Builds TLS ClientHello bytes for tests.</summary>
internal static class HelloBuilder
{
    public static byte[] ServerNameExtension(params string[] names)
    {
        var list = new List<byte>();
        foreach (var n in names)
        {
            var raw = Encoding.UTF8.GetBytes(n);
            list.Add(0);
            list.AddRange([(byte)(raw.Length >> 8), (byte)raw.Length]);
            list.AddRange(raw);
        }

        return Extension(0, [(byte)(list.Count >> 8), (byte)list.Count, .. list]);
    }

    public static byte[] Extension(int type, byte[] body) =>
        [(byte)(type >> 8), (byte)type, (byte)(body.Length >> 8), (byte)body.Length, .. body];

    /// <summary>A handshake message (type 1, 24-bit length) holding a ClientHello with the given extensions, without record framing.</summary>
    public static byte[] Handshake(params byte[][] extensions)
    {
        var ext = extensions.SelectMany(e => e).ToArray();
        var body = new List<byte> { 3, 3 };
        body.AddRange(new byte[32]);
        body.Add(0);                       // session id length
        body.AddRange([0, 2, 0x13, 0x01]); // one cipher suite
        body.AddRange([1, 0]);             // null compression
        if (extensions.Length > 0)
        {
            body.AddRange([(byte)(ext.Length >> 8), (byte)ext.Length]);
            body.AddRange(ext);
        }

        return [1, (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count, .. body];
    }

    public static byte[] Record(byte[] payload) =>
        [0x16, 3, 1, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    /// <summary>A hello in one record.</summary>
    public static byte[] Hello(params string[] names) =>
        Record(Handshake(names.Length == 0 ? [] : [ServerNameExtension(names)]));

    /// <summary>Splits the handshake over <paramref name="parts"/> records of roughly equal size.</summary>
    public static byte[] Split(byte[] handshake, int parts)
    {
        var size = (handshake.Length + parts - 1) / parts;
        return [.. handshake.Chunk(size).SelectMany(Record)];
    }
}

/// <summary>A proxy wired to fakes: scripted DNS, a local fake upstream and a capturing audit log.</summary>
internal sealed class ProxyRig : IAsyncDisposable
{
    public required AgentEgressProxy Proxy { get; init; }

    public required FakeUpstream Upstream { get; init; }

    public required ScriptedResolver Resolver { get; init; }

    public required RedirectingConnector Connector { get; init; }

    public required CapturingAuditLog Audit { get; init; }

    public IPEndPoint Endpoint => Proxy.LocalEndPoint!;

    public static ProxyRig Build(bool echo, string[] allow, Action<ScriptedResolver>? script = null, VmEgressOptions? options = null, ILocalAddressSource? localAddresses = null)
    {
        var upstream = new FakeUpstream(echo);
        var resolver = new ScriptedResolver();
        script?.Invoke(resolver);
        var connector = new RedirectingConnector(upstream.EndPoint);
        var audit = new CapturingAuditLog();
        var opts = options ?? new VmEgressOptions();
        var auditor = new EgressAuditor(audit, opts, TimeProvider.System);
        Assert.True(Allowlist.TryCompile(allow, out var list, out _));
        var proxy = new AgentEgressProxy("agent-1", IPAddress.Loopback, 0, list, opts, auditor, null, resolver, connector, localAddresses ?? new FixedLocalAddresses());
        proxy.Start();
        return new ProxyRig { Proxy = proxy, Upstream = upstream, Resolver = resolver, Connector = connector, Audit = audit };
    }

    /// <summary>Compiles and installs a new allowlist.</summary>
    public void SetAllowlist(params string[] allow)
    {
        Assert.True(Allowlist.TryCompile(allow, out var list, out _));
        Proxy.UpdateAllowlist(list);
    }

    public IEnumerable<string> DenyReasons => Audit.OfKind(EgressAuditor.DenyKind).Select(e => e.Payload);

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        await Upstream.DisposeAsync();
    }

    /// <summary>Polls until <paramref name="condition"/> holds or five seconds pass.</summary>
    public static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }
}
