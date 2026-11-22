using System.Threading.Channels;
using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeGuestPty : IGuestPty
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly object _gate = new();

    public ChannelReader<byte[]> Output => _output.Reader;

    public Task<int> Exited => _exit.Task;

    public List<byte[]> Writes { get; } = [];

    public List<(int Cols, int Rows)> Resizes { get; } = [];

    public bool Disposed { get; private set; }

    public bool FailWrites { get; set; }

    public byte[] AllWritten
    {
        get
        {
            lock (_gate)
            {
                return Writes.SelectMany(w => w).ToArray();
            }
        }
    }

    public void Emit(byte[] data) => _output.Writer.TryWrite(data);

    public void Emit(string text) => Emit(System.Text.Encoding.UTF8.GetBytes(text));

    public void Exit(int code)
    {
        _output.Writer.TryComplete();
        _exit.TrySetResult(code);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (FailWrites)
        {
            throw new IOException("guest write failed");
        }

        lock (_gate)
        {
            Writes.Add(bytes.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        lock (_gate)
        {
            Resizes.Add((cols, rows));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeChannel : IGuestPtyChannel
{
    public bool IsAvailable { get; set; } = true;

    public bool FailOpen { get; set; }

    public List<FakeGuestPty> Ptys { get; } = [];

    public FakeGuestPty Last => Ptys[^1];

    public Task<IGuestPty> OpenAsync(string vmId, string terminalId, int cols, int rows, CancellationToken ct)
    {
        if (FailOpen)
        {
            throw new IOException("vm gone");
        }

        var pty = new FakeGuestPty();
        lock (Ptys)
        {
            Ptys.Add(pty);
        }

        return Task.FromResult<IGuestPty>(pty);
    }
}

internal sealed class RecordingAudit : ITerminalAudit
{
    private readonly object _gate = new();
    private readonly List<(string Kind, string Member, string TerminalId)> _rows = [];

    public bool Fail { get; set; }

    public IReadOnlyList<string> Kinds
    {
        get
        {
            lock (_gate)
            {
                return _rows.Select(r => r.Kind).ToList();
            }
        }
    }

    public IReadOnlyList<(string Kind, string Member, string TerminalId)> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows.ToList();
            }
        }
    }

    public ValueTask RecordAsync(string kind, TerminalCaller caller, string terminalId, CancellationToken ct)
    {
        if (Fail)
        {
            throw new IOException("audit down");
        }

        lock (_gate)
        {
            _rows.Add((kind, caller.MemberId, terminalId));
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class TableGrants : ITerminalGrantSource
{
    public HashSet<string> View { get; } = [];

    public HashSet<string> Write { get; } = [];

    public bool Throw { get; set; }

    public bool IsViewGranted(string memberId, TerminalInfo terminal) =>
        Throw ? throw new InvalidOperationException("grants down") : View.Contains(memberId);

    public bool IsWriteGranted(string memberId, TerminalInfo terminal) =>
        Throw ? throw new InvalidOperationException("grants down") : Write.Contains(memberId);
}

internal sealed class AllowAllPolicy : ITerminalAccessPolicy
{
    public bool CanView(TerminalCaller caller, TerminalInfo terminal) => true;

    public bool CanWrite(TerminalCaller caller, TerminalInfo terminal) => true;
}

internal sealed class Harness : IAsyncDisposable
{
    public static readonly TerminalCaller Owner = new("owner", IsOwner: true, IsAgent: false);
    public static readonly TerminalCaller Agent1 = new("agent-1", IsOwner: false, IsAgent: true);
    public static readonly TerminalCaller Agent2 = new("agent-2", IsOwner: false, IsAgent: true);
    public static readonly TerminalCaller Human = new("human", IsOwner: false, IsAgent: false);

    public Harness(TerminalOptions? options = null, ITerminalAccessPolicy? policy = null)
    {
        Grants = new TableGrants();
        Broker = new TerminalBroker(
            Channel,
            policy ?? new DefaultTerminalAccessPolicy(Grants),
            Audit,
            options,
            Time);
    }

    public FakeChannel Channel { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public FakeTime Time { get; } = new();

    public TableGrants Grants { get; }

    public TerminalBroker Broker { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<TerminalInfo> OpenAsync(TerminalCaller? caller = null, string agentId = "agent-1")
    {
        var (result, info) = await Broker.OpenAsync(caller ?? Agent1, agentId, "vm-1", 80, 24, Ct);
        Assert.True(result.Ok, result.Refusal + " " + result.Detail);
        return info!;
    }

    public async Task<ITerminalSubscription> SubscribeAsync(TerminalCaller caller, string id, long from = 0)
    {
        var (result, sub) = await Broker.SubscribeAsync(caller, id, from, Ct);
        Assert.True(result.Ok, result.Refusal + " " + result.Detail);
        return sub!;
    }

    public static async Task<TerminalOutputChunk> ReadAsync(ITerminalSubscription sub)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await sub.Output.ReadAsync(cts.Token);
    }

    public static async Task<List<TerminalOutputChunk>> ReadAsync(ITerminalSubscription sub, int count)
    {
        var list = new List<TerminalOutputChunk>();
        for (var i = 0; i < count; i++)
        {
            list.Add(await ReadAsync(sub));
        }

        return list;
    }

    public static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5, Ct);
        }
    }

    public ValueTask DisposeAsync() => Broker.DisposeAsync();
}
