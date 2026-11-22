using System.Threading.Channels;
using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal;

internal enum TakeOutcome
{
    Taken,
    AlreadyHeld,
    HeldByOther,
}

internal enum ReleaseOutcome
{
    Released,
    NotHeld,
    Forbidden,
}

internal enum InputGrant
{
    Allowed,
    RateLimited,
}

/// <summary>One terminal session: ring, fan-out, writer role, input bucket and the guest pump. All state sits behind one gate.</summary>
internal sealed class TerminalSession : IDisposable
{
    private readonly object _gate = new();
    private readonly TerminalOptions _options;
    private readonly TimeProvider _time;
    private readonly List<Subscriber> _subscribers = [];
    private readonly Dictionary<string, Bucket> _buckets = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IGuestPty? _pty;
    private Task _pump = Task.CompletedTask;
    private string? _writer;
    private bool _ready;
    private bool _exited;
    private bool _humanInputAudited;
    private int _cols;
    private int _rows;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _removeAt = DateTimeOffset.MaxValue;

    public TerminalSession(string id, string agentId, string vmId, int cols, int rows, TerminalOptions options, TimeProvider time)
    {
        Id = id;
        AgentId = agentId;
        VmId = vmId;
        _cols = cols;
        _rows = rows;
        _options = options;
        _time = time;
        _lastActivity = time.GetUtcNow();
        Ring = new ScrollbackRing(options.ScrollbackBytes);
    }

    public string Id { get; }

    public string AgentId { get; }

    public string VmId { get; }

    public ScrollbackRing Ring { get; }

    public SemaphoreSlim InputGate { get; } = new(1, 1);

    public Task<int> ExitTask => _exit.Task;

    public IGuestPty? Pty
    {
        get
        {
            lock (_gate)
            {
                return _pty;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    public bool IsExited
    {
        get
        {
            lock (_gate)
            {
                return _exited;
            }
        }
    }

    public DateTimeOffset RemoveAt
    {
        get
        {
            lock (_gate)
            {
                return _removeAt;
            }
        }
    }

    public DateTimeOffset LastActivity
    {
        get
        {
            lock (_gate)
            {
                return _lastActivity;
            }
        }
    }

    public TerminalInfo Info()
    {
        lock (_gate)
        {
            return new TerminalInfo(Id, AgentId, VmId, _cols, _rows, _writer, Ring.LastSeq);
        }
    }

    public void Start(IGuestPty pty, string? writer)
    {
        lock (_gate)
        {
            _pty = pty;
            _writer = writer;
            _ready = true;
            _lastActivity = _time.GetUtcNow();
        }

        _pump = Task.Run(() => PumpAsync(pty));
    }

    public TakeOutcome TryTake(string member)
    {
        lock (_gate)
        {
            if (_exited)
            {
                return TakeOutcome.HeldByOther;
            }

            if (_writer is null)
            {
                _writer = member;
                _humanInputAudited = false;
                return TakeOutcome.Taken;
            }

            return string.Equals(_writer, member, StringComparison.Ordinal) ? TakeOutcome.AlreadyHeld : TakeOutcome.HeldByOther;
        }
    }

    public void ClearWriterIf(string member)
    {
        lock (_gate)
        {
            if (string.Equals(_writer, member, StringComparison.Ordinal))
            {
                _writer = null;
                _buckets.Remove(member);
            }
        }
    }

    public ReleaseOutcome TryRelease(string member, bool isOwner)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return ReleaseOutcome.NotHeld;
            }

            if (!isOwner && !string.Equals(_writer, member, StringComparison.Ordinal))
            {
                return ReleaseOutcome.Forbidden;
            }

            _buckets.Remove(_writer);
            _writer = null;
            return ReleaseOutcome.Released;
        }
    }

    public bool IsWriter(string member)
    {
        lock (_gate)
        {
            return !_exited && string.Equals(_writer, member, StringComparison.Ordinal);
        }
    }

    public bool ShouldAuditHumanInput(bool everyInput)
    {
        lock (_gate)
        {
            return everyInput || !_humanInputAudited;
        }
    }

    public void MarkHumanInputAudited()
    {
        lock (_gate)
        {
            _humanInputAudited = true;
        }
    }

    public void SetSize(int cols, int rows)
    {
        lock (_gate)
        {
            _cols = cols;
            _rows = rows;
            _lastActivity = _time.GetUtcNow();
        }
    }

    public void Dispose() => _cts.Dispose();

    public void Touch()
    {
        lock (_gate)
        {
            _lastActivity = _time.GetUtcNow();
        }
    }

    /// <summary>One-second token bucket per client. A write larger than the whole bucket is always refused.</summary>
    public InputGrant TryConsumeInput(string member, int bytes)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var rate = _options.InputBytesPerSecond;
            if (!_buckets.TryGetValue(member, out var bucket))
            {
                bucket = new Bucket { Tokens = rate, Last = now };
                _buckets[member] = bucket;
            }

            var elapsed = Math.Max(0, (now - bucket.Last).TotalSeconds);
            bucket.Tokens = Math.Min(rate, bucket.Tokens + (elapsed * rate));
            bucket.Last = now;
            if (bytes > rate || bucket.Tokens < bytes)
            {
                return InputGrant.RateLimited;
            }

            bucket.Tokens -= bytes;
            _lastActivity = now;
            return InputGrant.Allowed;
        }
    }

    public (ITerminalSubscription Subscription, long ReplayFrom) Subscribe(long fromSeq)
    {
        var channel = Channel.CreateBounded<TerminalOutputChunk>(new BoundedChannelOptions(_options.SubscriberQueueFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        var sub = new Subscriber(channel);
        lock (_gate)
        {
            var oldest = Ring.OldestSeq;
            var last = Ring.LastSeq;
            sub.NextSeq = fromSeq <= 0 ? oldest : Math.Clamp(fromSeq, oldest, last + 1);
            sub.Finishing = _exited;
            var replayFrom = sub.NextSeq;
            _subscribers.Add(sub);
            DeliverPending(sub, liveSeq: -1);
            return (new Subscription(this, sub), replayFrom);
        }
    }

    public void Detach(Subscriber sub)
    {
        lock (_gate)
        {
            sub.Detached = true;
            _subscribers.Remove(sub);
            sub.Channel.Writer.TryComplete();
        }
    }

    public void Refill(Subscriber sub)
    {
        lock (_gate)
        {
            DeliverPending(sub, liveSeq: -1);
        }
    }

    /// <summary>Ends the session: stops the pump, disposes the guest and completes every subscriber. Safe to call more than once.</summary>
    public async Task TerminateAsync(int code = -1)
    {
        IGuestPty? pty;
        lock (_gate)
        {
            pty = _pty;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        MarkExited(code);
        if (pty is not null)
        {
            try
            {
                await pty.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A guest that fails to dispose is already gone from the broker's point of view.
            }
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump never throws by design; this keeps shutdown quiet if it ever does.
        }

        Dispose();
    }

    private async Task PumpAsync(IGuestPty pty)
    {
        var code = -1;
        try
        {
            var reader = pty.Output;
            var exitTask = pty.Exited;
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                var wait = reader.WaitToReadAsync(ct).AsTask();
                var first = await Task.WhenAny(wait, exitTask).ConfigureAwait(false);
                if (first == exitTask)
                {
                    break;
                }

                if (!await wait.ConfigureAwait(false))
                {
                    break;
                }

                while (reader.TryRead(out var data))
                {
                    Publish(data);
                }
            }

            while (reader.TryRead(out var rest))
            {
                Publish(rest);
            }

            code = await exitTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            code = -1;
        }

        MarkExited(code);
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await pty.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already ended.
        }

        Dispose();
    }

    private void Publish(byte[] data)
    {
        var max = _options.MaxFramePayloadBytes;
        for (var offset = 0; offset < data.Length; offset += max)
        {
            var piece = data.AsSpan(offset, Math.Min(max, data.Length - offset));
            lock (_gate)
            {
                var seq = Ring.Append(piece);
                _lastActivity = _time.GetUtcNow();
                foreach (var sub in _subscribers)
                {
                    DeliverPending(sub, seq);
                }
            }
        }
    }

    private void MarkExited(int code)
    {
        lock (_gate)
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            _writer = null;
            _removeAt = _time.GetUtcNow() + _options.ExitLinger;
            foreach (var sub in _subscribers)
            {
                sub.Finishing = true;
                DeliverPending(sub, liveSeq: -1);
            }
        }

        _exit.TrySetResult(code);
    }

    // Caller holds _gate. Delivers ring chunks from sub.NextSeq until the subscriber's bounded queue is full. A chunk that
    // does not fit is not lost: it stays in the ring and is delivered (marked as replay) on the next frame or read.
    private void DeliverPending(Subscriber sub, long liveSeq)
    {
        if (sub.Detached)
        {
            return;
        }

        var oldest = Ring.OldestSeq;
        if (sub.NextSeq < oldest)
        {
            sub.NextSeq = oldest;
        }

        var last = Ring.LastSeq;
        var retried = false;
        while (sub.NextSeq <= last)
        {
            if (!Ring.TryGet(sub.NextSeq, out var bytes))
            {
                sub.NextSeq++;
                continue;
            }

            if (sub.Channel.Writer.TryWrite(new TerminalOutputChunk(sub.NextSeq, bytes, sub.NextSeq != liveSeq)))
            {
                sub.NextSeq++;
                retried = false;
                continue;
            }

            // Queue full. Flag first, then re-check: if the reader drained in between it may have missed the flag.
            sub.Lagging = true;
            if (!retried && sub.Channel.Reader.Count == 0)
            {
                retried = true;
                continue;
            }

            break;
        }

        sub.Lagging = sub.NextSeq <= last;
        if (!sub.Lagging && sub.Finishing)
        {
            sub.Channel.Writer.TryComplete();
        }
    }

    private sealed class Bucket
    {
        public double Tokens { get; set; }

        public DateTimeOffset Last { get; set; }
    }

    internal sealed class Subscriber
    {
        private volatile bool _lagging;

        public Subscriber(Channel<TerminalOutputChunk> channel) => Channel = channel;

        public Channel<TerminalOutputChunk> Channel { get; }

        public long NextSeq { get; set; }

        public bool Finishing { get; set; }

        public bool Detached { get; set; }

        public bool Lagging
        {
            get => _lagging;
            set => _lagging = value;
        }
    }

    private sealed class Subscription : ITerminalSubscription
    {
        private readonly TerminalSession _session;
        private readonly Subscriber _sub;

        public Subscription(TerminalSession session, Subscriber sub)
        {
            _session = session;
            _sub = sub;
            Output = new SubscriberReader(session, sub);
        }

        public ChannelReader<TerminalOutputChunk> Output { get; }

        public Task<int> Exited => _session._exit.Task;

        public ValueTask DisposeAsync()
        {
            _session.Detach(_sub);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Reads the bounded queue and refills it from the ring whenever a slow viewer makes room.</summary>
    private sealed class SubscriberReader : ChannelReader<TerminalOutputChunk>
    {
        private readonly TerminalSession _session;
        private readonly Subscriber _sub;

        public SubscriberReader(TerminalSession session, Subscriber sub)
        {
            _session = session;
            _sub = sub;
        }

        public override Task Completion => _sub.Channel.Reader.Completion;

        public override bool TryRead(out TerminalOutputChunk item)
        {
            if (!_sub.Channel.Reader.TryRead(out item))
            {
                return false;
            }

            if (_sub.Lagging)
            {
                _session.Refill(_sub);
            }

            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            _sub.Channel.Reader.WaitToReadAsync(cancellationToken);
    }
}
