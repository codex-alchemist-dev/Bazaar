using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bazaar.Contracts.Terminal;
using Bazaar.Server.Terminal.Abstractions;
using Bazaar.Server.Terminal.Stream;

namespace Bazaar.Server.Terminal.Tests.Stream;

internal sealed class FakeResolver : ITerminalTargetResolver
{
    public bool Refuse { get; set; }

    public ValueTask<TerminalTarget?> ResolveAsync(TerminalCaller caller, string? agentId, CancellationToken ct) =>
        ValueTask.FromResult(Refuse ? null : new TerminalTarget(agentId ?? caller.MemberId, "vm-1"));
}

/// <summary>Wraps a broker and counts live subscriptions so tests can prove nothing is left attached.</summary>
internal sealed class TrackingBroker : ITerminalBroker
{
    private readonly ITerminalBroker _inner;
    private int _live;
    private int _total;

    public TrackingBroker(ITerminalBroker inner) => _inner = inner;

    public int Live => Volatile.Read(ref _live);

    public int Total => Volatile.Read(ref _total);

    public Task<(TerminalResult Result, TerminalInfo? Terminal)> OpenAsync(TerminalCaller caller, string agentId, string vmId, int cols, int rows, CancellationToken ct) =>
        _inner.OpenAsync(caller, agentId, vmId, cols, rows, ct);

    public IReadOnlyList<TerminalInfo> List(TerminalCaller caller) => _inner.List(caller);

    public async Task<(TerminalResult Result, ITerminalSubscription? Subscription)> SubscribeAsync(TerminalCaller caller, string terminalId, long fromSeq, CancellationToken ct)
    {
        var (result, sub) = await _inner.SubscribeAsync(caller, terminalId, fromSeq, ct);
        if (sub is null)
        {
            return (result, null);
        }

        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _total);
        return (result, new Tracked(sub, () => Interlocked.Decrement(ref _live)));
    }

    public Task<TerminalResult> WriteAsync(TerminalCaller caller, string terminalId, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        _inner.WriteAsync(caller, terminalId, bytes, ct);

    public Task<TerminalResult> ResizeAsync(TerminalCaller caller, string terminalId, int cols, int rows, CancellationToken ct) =>
        _inner.ResizeAsync(caller, terminalId, cols, rows, ct);

    public Task<TerminalResult> TakeControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct) =>
        _inner.TakeControlAsync(caller, terminalId, ct);

    public Task<TerminalResult> ReleaseControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct) =>
        _inner.ReleaseControlAsync(caller, terminalId, ct);

    public Task<TerminalResult> CloseAsync(TerminalCaller caller, string terminalId, CancellationToken ct) =>
        _inner.CloseAsync(caller, terminalId, ct);

    private sealed class Tracked : ITerminalSubscription
    {
        private readonly ITerminalSubscription _sub;
        private readonly Action _onDispose;
        private int _disposed;

        public Tracked(ITerminalSubscription sub, Action onDispose)
        {
            _sub = sub;
            _onDispose = onDispose;
        }

        public ChannelReader<TerminalOutputChunk> Output => _sub.Output;

        public Task<int> Exited => _sub.Exited;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDispose();
            }

            await _sub.DisposeAsync();
        }
    }
}

internal sealed class StreamHarness : IAsyncDisposable
{
    public StreamHarness(TerminalCaller? caller = null, TerminalConnectionOptions? options = null, TerminalOptions? brokerOptions = null)
    {
        Inner = new Harness(brokerOptions);
        Broker = new TrackingBroker(Inner.Broker);
        Caller = caller ?? Harness.Agent1;
        Connection = new TerminalConnection(Broker, Caller, Resolver, options);
    }

    public Harness Inner { get; }

    public TrackingBroker Broker { get; }

    public TerminalCaller Caller { get; }

    public FakeResolver Resolver { get; } = new();

    public TerminalConnection Connection { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    public static byte[] Bytes(TerminalFrame frame) => TerminalFrameCodec.Encode(frame).Bytes!;

    public ValueTask SendAsync(TerminalFrame frame) => Connection.HandleInboundAsync(Bytes(frame), Ct);

    public ValueTask SendRawAsync(string json) => Connection.HandleInboundAsync(Json(json), Ct);

    public async Task<TerminalFrame> NextAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var raw = await Connection.Outbound.ReadAsync(cts.Token);
        var decoded = TerminalFrameCodec.Decode(raw.Span);
        Assert.True(decoded.IsSuccess, decoded.Detail);
        return decoded.Frame!;
    }

    public async Task<T> NextAsync<T>()
        where T : TerminalFrame
    {
        var frame = await NextAsync();
        return Assert.IsType<T>(frame);
    }

    public List<TerminalFrame> Drain()
    {
        var list = new List<TerminalFrame>();
        while (Connection.Outbound.TryRead(out var raw))
        {
            list.Add(TerminalFrameCodec.Decode(raw.Span).Frame!);
        }

        return list;
    }

    public async Task<string> OpenTerminalAsync(int cols = 80, int rows = 24)
    {
        await SendAsync(new TerminalOpen("req", null, cols, rows));
        var opened = await NextAsync<TerminalOpened>();
        return opened.TerminalId;
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Inner.DisposeAsync();
    }

    public static string Text(TerminalOutput output) => Encoding.UTF8.GetString(output.Bytes);

    public static string RawJson(object o) => JsonSerializer.Serialize(o);
}
