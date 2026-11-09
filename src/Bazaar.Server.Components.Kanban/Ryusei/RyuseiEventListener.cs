using System.Text.Json;
using System.Threading.Channels;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Kanban.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// Feeds the event bridge from the two NOTIFY channels that carry events in the repo today (doc 38, WP-R6). Two sources:
/// <list type="bullet">
/// <item><description>GC events (<c>gc</c>): a <see cref="GcNotifyListener"/> subscription. Each hint is the newest seq of a GC,
/// so the listener reads that GC's <c>gc_event</c> rows from its cursor to the hint. Council decisions and new messages both
/// arrive here.</description></item>
/// <item><description>Task events (<c>kanban_event</c>, doc 12): this class holds its own <c>LISTEN</c> connection, because
/// <see cref="GcNotifyListener"/> listens only to <c>gc</c>. Each hint names one task event seq, which is read by seq.</description></item>
/// </list>
/// Notifications are queued and drained by one worker, so the bridge never runs on Npgsql's reader thread.
/// </summary>
/// <remarks>
/// Known gaps (see the WP-R6 report): the GC cursor for a GC is set on first sight to its newest seq, so events before the
/// process started are not replayed. A kanban hint sent while the connection is down is not replayed. PR and Vault sources
/// have no channel yet, so nothing feeds them.
/// </remarks>
public sealed partial class RyuseiEventListener : IHostedService, IAsyncDisposable
{
    /// <summary>How long a GC subscription waits before it loops again, so cancellation is noticed without a wake.</summary>
    private static readonly TimeSpan _gcWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource _dataSource;
    private readonly RyuseiEventBridge _bridge;
    private readonly GcNotifyListener _gc;
    private readonly GcListenerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RyuseiEventListener> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<long> _taskEventSeqs = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });

    // Only the GC follower task reads and writes this, so it needs no lock.
    private readonly Dictionary<Guid, long> _gcCursors = [];
    private Task? _kanbanListen;
    private Task? _kanbanWorker;
    private Task? _gcFollower;

    public RyuseiEventListener(
        NpgsqlDataSource dataSource,
        RyuseiEventBridge bridge,
        GcNotifyListener gc,
        GcListenerOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<RyuseiEventListener>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(gc);
        _dataSource = dataSource;
        _bridge = bridge;
        _gc = gc;
        _options = options ?? GcListenerOptions.Default;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RyuseiEventListener>.Instance;
    }

    /// <summary>Parses a <c>kanban_event</c> payload (<c>{"task_id","seq","kind"}</c>) and returns its seq.</summary>
    public static bool TryParseTaskEventSeq(string payload, out long seq)
    {
        seq = 0;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("seq", out var s)
                && s.TryGetInt64(out seq);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Starts the three background loops. Idempotent.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _gcFollower ??= Task.Run(() => FollowGcAsync(_stop.Token), CancellationToken.None);
            _kanbanWorker ??= Task.Run(() => DrainTaskEventsAsync(_stop.Token), CancellationToken.None);
            _kanbanListen ??= Task.Run(() => ListenTaskEventsAsync(_stop.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] running;
        lock (_gate)
        {
            running = new[] { _gcFollower, _kanbanWorker, _kanbanListen }.OfType<Task>().ToArray();
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task FollowGcAsync(CancellationToken ct)
    {
        using var subscription = _gc.Subscribe();
        while (!ct.IsCancellationRequested)
        {
            GcWake? wake;
            try
            {
                wake = await subscription.WaitAsync(_gcWaitTimeout, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (wake is null)
            {
                continue;
            }

            var heads = new Dictionary<Guid, long>(wake.Heads);
            if (wake.Resync)
            {
                // The listener reconnected, so hints may have been missed. Each GC this follower has seen is checked against its head.
                foreach (var (gcId, head) in await ReadGcHeadsAsync(_gcCursors.Keys.ToArray(), ct).ConfigureAwait(false))
                {
                    heads[gcId] = Math.Max(heads.GetValueOrDefault(gcId), head);
                }
            }

            foreach (var (gcId, head) in heads)
            {
                try
                {
                    await ProcessGcAsync(gcId, head, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException)
                {
                    // The cursor did not move, so the next hint for this GC retries from the same place. Re-running a row is safe:
                    // a fired event trigger is no longer active, and a watcher keeps the deadline it already has.
                    LogGcFailed(_logger, gcId, ex);
                }
            }
        }
    }

    private async Task ProcessGcAsync(Guid gcId, long head, CancellationToken ct)
    {
        // A GC seen for the first time starts at its hinted head, so history before this process is not replayed.
        var from = _gcCursors.TryGetValue(gcId, out var cursor) ? cursor : head - 1;
        if (head <= from)
        {
            return;
        }

        foreach (var row in await ReadGcEventsAsync(gcId, from, head, ct).ConfigureAwait(false))
        {
            var envelope = RyuseiEventMapping.FromGcEvent(row.Kind, gcId, row.AuthorKind, row.AuthorId, row.Seq);
            if (envelope is not null)
            {
                await _bridge.HandleAsync(envelope, _time.GetUtcNow(), ct).ConfigureAwait(false);
            }
        }

        _gcCursors[gcId] = head;
    }

    private async Task<IReadOnlyList<GcEventRow>> ReadGcEventsAsync(Guid gcId, long from, long to, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("""
            SELECT seq, kind, author_kind, author_id FROM gc_event
            WHERE gc_id = @gc_id AND seq > @from AND seq <= @to
            ORDER BY seq
            """);
        cmd.Parameters.AddWithValue("gc_id", gcId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        var rows = new List<GcEventRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GcEventRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3)));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, long>> ReadGcHeadsAsync(Guid[] gcIds, CancellationToken ct)
    {
        var heads = new Dictionary<Guid, long>();
        if (gcIds.Length == 0)
        {
            return heads;
        }

        await using var cmd = _dataSource.CreateCommand("SELECT gc_id, head_seq FROM gc WHERE gc_id = ANY(@gc_ids)");
        cmd.Parameters.AddWithValue("gc_ids", gcIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            heads[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return heads;
    }

    private async Task ListenTaskEventsAsync(CancellationToken ct)
    {
        var delay = _options.ReconnectMin;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                conn.Notification += (_, e) =>
                {
                    if (TryParseTaskEventSeq(e.Payload, out var seq))
                    {
                        _taskEventSeqs.Writer.TryWrite(seq);
                    }
                    else
                    {
                        LogBadPayload(_logger);
                    }
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {TaskEventRepository.NotifyChannel}", conn))
                {
                    await listen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                delay = _options.ReconnectMin;
                await HoldAsync(conn, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException or ObjectDisposedException)
            {
                LogListenLost(_logger, delay, ex);
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectMax.Ticks));
        }
    }

    // Waits for notifications; each idle heartbeat runs a trivial query so a dead socket raises instead of hanging.
    private async Task HoldAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var beat = new CancellationTokenSource(_options.Heartbeat, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, beat.Token);
            try
            {
                await conn.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await using var ping = new NpgsqlCommand("SELECT 1", conn);
                await ping.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DrainTaskEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var seq in _taskEventSeqs.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessTaskEventAsync(seq, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException)
                {
                    LogTaskEventFailed(_logger, seq, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task ProcessTaskEventAsync(long seq, CancellationToken ct)
    {
        string kind;
        Guid taskId;
        Guid projectId;
        string payload;
        await using (var cmd = _dataSource.CreateCommand("""
            SELECT e.kind, e.task_id, t.project_id, e.payload::text
            FROM task_events AS e
            JOIN tasks AS t ON t.id = e.task_id
            WHERE e.seq = @seq
            """))
        {
            cmd.Parameters.AddWithValue("seq", seq);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            kind = reader.GetString(0);
            taskId = reader.GetGuid(1);
            projectId = reader.GetGuid(2);
            payload = reader.GetString(3);
        }

        var envelope = RyuseiEventMapping.FromTaskEvent(kind, projectId, taskId, payload);
        if (envelope is not null)
        {
            await _bridge.HandleAsync(envelope, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
    }

    private sealed record GcEventRow(long Seq, string Kind, string AuthorKind, Guid AuthorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event listener connection lost; reconnecting (next wait {Delay}).")]
    private static partial void LogListenLost(ILogger logger, TimeSpan delay, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a kanban event notification with an unreadable payload.")]
    private static partial void LogBadPayload(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event bridge could not read GC {GcId}; it retries on the next hint.")]
    private static partial void LogGcFailed(ILogger logger, Guid gcId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event bridge could not process task event seq {Seq}.")]
    private static partial void LogTaskEventFailed(ILogger logger, long seq, Exception ex);
}
