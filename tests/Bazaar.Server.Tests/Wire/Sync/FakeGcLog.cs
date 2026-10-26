using System.Threading.Channels;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Tests.Wire.Sync;

/// <summary>In-memory GC log with the counters of doc 02: <c>n</c> per entry, <c>ev</c> per message version.</summary>
internal sealed class FakeGcLog : IGcStreamSource, IGcAccessPolicy, IGcNotificationSource
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Gc> _gcs = [];
    private readonly Channel<GcNotification> _notes = Channel.CreateUnbounded<GcNotification>();
    private readonly HashSet<(Guid Principal, Guid Gc)> _members = [];
    private int _failReads;

    /// <summary>When true, appends commit without a notification (a lost NOTIFY).</summary>
    public bool DropNotifications { get; set; }

    public int ReadCalls { get; private set; }

    public void FailNextReads(int count) => Interlocked.Exchange(ref _failReads, count);

    public void AddMember(Guid principal, Guid gc) { lock (_gate) { _members.Add((principal, gc)); } }

    public void RemoveMember(Guid principal, Guid gc) { lock (_gate) { _members.Remove((principal, gc)); } }

    public void CreateGc(Guid gc) { lock (_gate) { _gcs.TryAdd(gc, new Gc()); } }

    public WireGcMessage Append(Guid gc, string text)
    {
        WireGcMessage message;
        lock (_gate)
        {
            var g = _gcs[gc];
            g.N++;
            g.Ev++;
            message = new WireGcMessage(gc, g.N, g.Ev, Guid.NewGuid(), Guid.Empty, text, DateTimeOffset.UnixEpoch, false, [], null);
            g.Current[message.MessageId] = message;
        }

        Notify(gc, message.N);
        return message;
    }

    public WireGcMessage Edit(Guid gc, Guid messageId, string text)
    {
        WireGcMessage message;
        lock (_gate)
        {
            var g = _gcs[gc];
            g.Ev++;
            var old = g.Current[messageId];
            message = old with { Ev = g.Ev, Text = text, Edited = true };
            g.Current[messageId] = message;
        }

        Notify(gc, message.N);
        return message;
    }

    public IReadOnlyList<WireGcMessage> Snapshot(Guid gc)
    {
        lock (_gate) { return [.. _gcs[gc].Current.Values.OrderBy(m => m.Ev)]; }
    }

    public void Notify(Guid gc, long n)
    {
        if (!DropNotifications)
        {
            _notes.Writer.TryWrite(new GcNotification(gc, n));
        }
    }

    public void Reconnected() => _notes.Writer.TryWrite(GcNotification.ReconnectMarker);

    public ValueTask<GcHead?> GetHeadAsync(Guid gcId, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<GcHead?>(_gcs.TryGetValue(gcId, out var g) ? new GcHead(g.N, g.Ev) : null);
        }
    }

    public ValueTask<IReadOnlyList<WireGcMessage>> ReadAsync(
        Guid gcId, long after, long editAfter, long afterEv, int limit, CancellationToken ct)
    {
        // Injected failures hit reads that start at the beginning of the log (a replay, or the pump of a new GC).
        if (afterEv == 0 && Interlocked.Decrement(ref _failReads) >= 0)
        {
            throw new InvalidOperationException("injected read failure");
        }

        Interlocked.CompareExchange(ref _failReads, 0, -1);
        lock (_gate)
        {
            ReadCalls++;
            IReadOnlyList<WireGcMessage> page = [.. _gcs[gcId].Current.Values
                .Where(m => (m.N > after || m.Ev > editAfter) && m.Ev > afterEv)
                .OrderBy(m => m.Ev)
                .Take(limit)];
            return ValueTask.FromResult(page);
        }
    }

    public ValueTask<IReadOnlyList<WireGcMessage>> ReadTailAsync(Guid gcId, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<WireGcMessage> tail = [.. _gcs[gcId].Current.Values.OrderBy(m => m.N).TakeLast(limit)];
            return ValueTask.FromResult(tail);
        }
    }

    public ValueTask<int> CountUnreadAsync(Guid gcId, Guid viewerId, CancellationToken ct) => ValueTask.FromResult(7);

    public ValueTask<bool> CanReadAsync(Guid principalId, Guid gcId, CancellationToken ct)
    {
        lock (_gate) { return ValueTask.FromResult(_members.Contains((principalId, gcId))); }
    }

    public IAsyncEnumerable<GcNotification> ReadAllAsync(CancellationToken ct) => _notes.Reader.ReadAllAsync(ct);

    private sealed class Gc
    {
        public long N;
        public long Ev;
        public Dictionary<Guid, WireGcMessage> Current { get; } = [];
    }
}
