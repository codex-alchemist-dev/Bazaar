using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bazaar.Server.Security.Audit;
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
