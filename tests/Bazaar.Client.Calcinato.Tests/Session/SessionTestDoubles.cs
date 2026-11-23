using System.Diagnostics;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Tests.Session;

/// <summary>A byte stream the test drives directly. Records writes, resizes and disposal.</summary>
internal sealed class FakeTerminalSource(bool isRemote = false, bool canWrite = true) : ITerminalSource
{
    private readonly Channel<TerminalChunk> _output = Channel.CreateUnbounded<TerminalChunk>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte[]> _writes = [];
    private readonly List<(int Cols, int Rows)> _resizes = [];
    private readonly object _gate = new();

    public bool IsRemote { get; } = isRemote;

    public bool CanWrite { get; set; } = canWrite;

    public bool Disposed { get; private set; }

    public ChannelReader<TerminalChunk> Output => _output.Reader;

    public Task<int> Exited => _exited.Task;

    public IReadOnlyList<byte[]> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }
    }

    public IReadOnlyList<(int Cols, int Rows)> Resizes
    {
        get
        {
            lock (_gate)
            {
                return _resizes.ToArray();
            }
        }
    }

    public void Emit(byte[] bytes, bool replay = false) => _output.Writer.TryWrite(new TerminalChunk(0, bytes, replay));

    public void Emit(string text) => Emit(System.Text.Encoding.UTF8.GetBytes(text));

    public void Finish(int code)
    {
        _exited.TrySetResult(code);
        _output.Writer.TryComplete();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        lock (_gate)
        {
            _writes.Add(bytes.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        lock (_gate)
        {
            _resizes.Add((cols, rows));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _exited.TrySetResult(-1);
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A head host that hands out <see cref="FakeTerminalSource"/> instances and records what was opened.</summary>
internal sealed class FakeTerminalHost(bool supportsLocal) : ITerminalHost
{
    public bool SupportsLocal { get; } = supportsLocal;

    public List<TerminalSpec> Opened { get; } = [];

    public List<FakeTerminalSource> LocalSources { get; } = [];

    public List<string> AttachedIds { get; } = [];

    public List<FakeTerminalSource> RemoteSources { get; } = [];

    /// <summary>Whether attached viewers have the writer role. Default true.</summary>
    public bool RemoteCanWrite { get; set; } = true;

    public ITerminalSource OpenLocal(TerminalSpec spec)
    {
        if (!SupportsLocal)
        {
            throw new NotSupportedException("This head has no local terminal backend.");
        }

        Opened.Add(spec);
        var source = new FakeTerminalSource();
        LocalSources.Add(source);
        return source;
    }

    public Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct)
    {
        AttachedIds.Add(terminalId);
        var source = new FakeTerminalSource(isRemote: true, canWrite: RemoteCanWrite);
        RemoteSources.Add(source);
        return Task.FromResult<ITerminalSource>(source);
    }
}

/// <summary>
/// A clock the test moves by hand. Timers fire only when <see cref="Advance"/> passes their due time, so rate-limit
/// tests do not depend on wall-clock timing.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private long _timestamp;

    public override long TimestampFrequency => Stopwatch.Frequency;

    /// <summary>Timers created and not yet fired or disposed.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <summary>Moves the clock forward and fires every timer that is now due.</summary>
    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _timestamp += ToTimestamp(by);
            due = _timers.Where(timer => timer.DueAt <= _timestamp).ToList();
            foreach (FakeTimer timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (FakeTimer timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new FakeTimer(this, callback, state, _timestamp + ToTimestamp(dueTime));
            _timers.Add(timer);
            return timer;
        }
    }

    private static long ToTimestamp(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state, long dueAt) : ITimer
    {
        public long DueAt { get; } = dueAt;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("The fake clock creates one-shot timers only.");

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
