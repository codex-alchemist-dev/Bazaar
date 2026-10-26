using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Gc.Write;

/// <summary>How the notifier batches.</summary>
/// <param name="CoalesceWindow">How long to gather newer seqs after the first one arrives, before one flush.</param>
/// <param name="MaxRetryDelay">Longest wait between flush attempts while the database is unreachable.</param>
public sealed record GcNotifierOptions(TimeSpan CoalesceWindow, TimeSpan MaxRetryDelay)
{
    /// <summary>5 ms window (spike S3), retry back-off up to 2 s.</summary>
    public static GcNotifierOptions Default { get; } = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
}

/// <summary>
/// Sends GC wake hints after commit (spike S3). Keeps the newest seq per GC and, a few milliseconds after the first one
/// arrives, sends every pending hint in one transaction. A hint that cannot be sent is kept and retried; if the process dies
/// first the hint is lost, which costs latency only, because consumers also sweep head against cursor.
/// </summary>
public sealed partial class CoalescingGcNotifier : IGcNotifier, IHostedService, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly GcNotifierOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CoalescingGcNotifier> _logger;
    private readonly Lock _gate = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private Dictionary<Guid, long> _pending = [];
    private Task? _loop;
    private int _flushes;

    public CoalescingGcNotifier(
        NpgsqlDataSource dataSource, GcNotifierOptions? options = null, TimeProvider? timeProvider = null, ILogger<CoalescingGcNotifier>? logger = null)
    {
        _dataSource = dataSource;
        _options = options ?? GcNotifierOptions.Default;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CoalescingGcNotifier>.Instance;
    }

    /// <summary>How many flushes have sent at least one hint. For tests and metrics.</summary>
    public int FlushCount => Volatile.Read(ref _flushes);

    /// <inheritdoc />
    public void Notify(Guid gcId, long seq)
    {
        lock (_gate)
            _pending[gcId] = _pending.TryGetValue(gcId, out var old) && old > seq ? old : seq;
        _wake.Writer.TryWrite(true);
    }

    /// <summary>Starts the background flush loop. Idempotent.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the loop and sends what is pending, best effort.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_gate)
            loop = _loop;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or OperationCanceledException)
        {
            LogFlushFailed(_logger, ex);
        }
    }

    /// <summary>Sends every pending hint now, in one transaction. Throws if the database is unreachable (the hints stay pending).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        Dictionary<Guid, long> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;
            batch = _pending;
            _pending = [];
        }

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmds = new NpgsqlBatch(conn);
            foreach (var (gc, seq) in batch)
            {
                var cmd = new NpgsqlBatchCommand("SELECT pg_notify($1, $2)");
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = GcNotifyChannel.Name });
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = GcNotifyChannel.FormatPayload(gc, seq) });
                cmds.BatchCommands.Add(cmd);
            }

            await cmds.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _flushes);
        }
        catch
        {
            Requeue(batch);
            throw;
        }
    }

    private void Requeue(Dictionary<Guid, long> batch)
    {
        lock (_gate)
        {
            foreach (var (gc, seq) in batch)
                _pending[gc] = _pending.TryGetValue(gc, out var newer) && newer > seq ? newer : seq;
        }

        _wake.Writer.TryWrite(true);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retry = TimeSpan.Zero;
        try
        {
            while (await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _wake.Reader.TryRead(out _);
                await Task.Delay(retry > TimeSpan.Zero ? retry : _options.CoalesceWindow, _time, ct).ConfigureAwait(false);
                try
                {
                    await FlushAsync(ct).ConfigureAwait(false);
                    retry = TimeSpan.Zero;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    retry = retry == TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, _options.MaxRetryDelay.Ticks));
                    LogFlushRetry(_logger, retry, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GC notify flush failed; hints stay pending and are retried in {Retry}.")]
    private static partial void LogFlushRetry(ILogger logger, TimeSpan retry, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GC notify flush at shutdown failed; consumers will catch up through their sweep.")]
    private static partial void LogFlushFailed(ILogger logger, Exception ex);
}
