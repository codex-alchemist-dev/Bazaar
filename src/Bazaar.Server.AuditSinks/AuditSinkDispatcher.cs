using System.Collections.Concurrent;
using System.Globalization;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditSinks;

/// <summary>Settings for <see cref="AuditSinkDispatcher"/>. All operator settings with safe defaults.</summary>
public sealed record AuditSinkDispatcherOptions
{
    /// <summary>Rows or checkpoints read per batch.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Batches delivered to one sink in one pass, so one sink that is far behind cannot hold a pass open.</summary>
    public int MaxBatchesPerPass { get; init; } = 20;

    /// <summary>First retry delay after a failure; it doubles per consecutive failure.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest retry delay.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Pause between passes of <see cref="AuditSinkDispatcher.RunAsync"/>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Fans committed audit rows and checkpoints out to the secondary sinks from a per-sink cursor (doc 48 section 5.4).
/// It only reads the chain, never writes it, so the primary chain cannot be blocked by a sink. A failing sink is
/// isolated: it backs off on its own clock, the failure is written to the audit log, and the other sinks carry on.
/// Delivery is at-least-once: the cursor moves after a batch is accepted, so a crash in between re-sends it, and the
/// idempotency keys on every row and checkpoint let a sink drop the repeat.
/// </summary>
public sealed class AuditSinkDispatcher : IDisposable
{
    private readonly IAuditSinkSource _source;
    private readonly IAuditSinkCursorStore _cursors;
    private readonly IReadOnlyList<IAuditSink> _sinks;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly AuditSinkDispatcherOptions _options;
    private readonly ConcurrentDictionary<string, SinkState> _state = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pass = new(1, 1);

    public AuditSinkDispatcher(
        IAuditSinkSource source,
        IAuditSinkCursorStore cursors,
        IEnumerable<IAuditSink> sinks,
        IAuditLog audit,
        TimeProvider time,
        AuditSinkDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        _source = source;
        _cursors = cursors;
        _sinks = sinks.ToList();
        _audit = audit;
        _time = time;
        _options = options ?? new AuditSinkDispatcherOptions();
        if (_options.BatchSize < 1 || _options.MaxBatchesPerPass < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Batch size and batches per pass must be at least 1.");
        }

        var duplicate = _sinks.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two audit sinks are named '{duplicate.Key}'.", nameof(sinks));
        }
    }

    public void Dispose() => _pass.Dispose();

    /// <summary>Consecutive failures of a sink; zero when it is healthy.</summary>
    public int FailureCount(string sinkName) => _state.TryGetValue(sinkName, out var s) ? s.Failures : 0;

    /// <summary>Delivers once to every sink that is due. Sinks run in parallel and one failing sink never fails the pass.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_sinks.Select(s => PumpAsync(s, ct))).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>Runs passes until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _time);
        try
        {
            do
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task PumpAsync(IAuditSink sink, CancellationToken ct)
    {
        var state = _state.GetOrAdd(sink.Name, _ => new SinkState());
        if (_time.GetUtcNow() < state.NextAttempt)
        {
            return;
        }

        try
        {
            var cursor = await _cursors.GetAsync(sink.Name, ct).ConfigureAwait(false);
            for (var i = 0; i < _options.MaxBatchesPerPass; i++)
            {
                var rows = await _source.ReadRowsAfterAsync(cursor.LastRowSeq, _options.BatchSize, ct).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    break;
                }

                await sink.DeliverRowsAsync(rows, ct).ConfigureAwait(false);
                cursor = cursor with { LastRowSeq = rows[^1].Seq };
                await _cursors.SaveAsync(sink.Name, cursor, ct).ConfigureAwait(false);
            }

            for (var i = 0; i < _options.MaxBatchesPerPass; i++)
            {
                var checkpoints = await _source.ReadCheckpointsAfterAsync(cursor.LastCheckpointSeq, _options.BatchSize, ct).ConfigureAwait(false);
                if (checkpoints.Count == 0)
                {
                    break;
                }

                await sink.DeliverCheckpointsAsync(checkpoints, ct).ConfigureAwait(false);
                cursor = cursor with { LastCheckpointSeq = checkpoints[^1].LastSeq };
                await _cursors.SaveAsync(sink.Name, cursor, ct).ConfigureAwait(false);
            }

            var recovered = state.Failures > 0;
            state.Failures = 0;
            state.NextAttempt = DateTimeOffset.MinValue;
            if (recovered)
            {
                await TryAuditAsync("audit.sink.recovered", sink.Name, 0, null, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Failures++;
            state.NextAttempt = _time.GetUtcNow() + Backoff(state.Failures);
            await TryAuditAsync("audit.sink.failed", sink.Name, state.Failures, ex.GetType().Name, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan Backoff(int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures - 1, 30));
        var ticks = Math.Min((double)_options.MaxBackoff.Ticks, _options.InitialBackoff.Ticks * factor);
        return TimeSpan.FromTicks((long)ticks);
    }

    // The failure record names the sink, the attempt and the exception type only: exception messages can carry
    // endpoints or credentials. If the audit write itself fails, the sink still backs off and nothing else is hurt.
    private async Task TryAuditAsync(string kind, string sink, int attempt, string? error, CancellationToken ct)
    {
        try
        {
            var payload = AuditSinkJson.Object(
            [
                new("sink", sink),
                new("attempt", attempt.ToString(CultureInfo.InvariantCulture)),
                new("error", error ?? string.Empty),
            ]);
            await _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), "audit-sinks", kind, payload), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately swallowed: the audit failure must not take the dispatcher down.
        }
    }

    private sealed class SinkState
    {
        public int Failures { get; set; }

        public DateTimeOffset NextAttempt { get; set; } = DateTimeOffset.MinValue;
    }
}
