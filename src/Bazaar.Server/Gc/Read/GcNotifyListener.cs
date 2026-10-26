using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Gc.Read;

/// <summary>How the listener holds its connection.</summary>
/// <param name="ReconnectMin">First wait after the connection drops.</param>
/// <param name="ReconnectMax">Longest wait between reconnect attempts.</param>
/// <param name="Heartbeat">Idle time after which the listener checks its connection, so a half-open socket is noticed.</param>
public sealed record GcListenerOptions(TimeSpan ReconnectMin, TimeSpan ReconnectMax, TimeSpan Heartbeat)
{
    public static GcListenerOptions Default { get; } = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
}

/// <summary>
/// The process's single <c>LISTEN gc</c> connection (docs/design/04, spike S3). It turns notifications into per-subscriber wakes.
/// NOTIFY is delivered only to a live connection, so after every reconnect (once <c>LISTEN</c> is back) it sends each subscriber a
/// resync wake: whatever committed while the connection was down is then found by reading the log from the cursor, not from the
/// hint. Must not sit behind a transaction-mode pooler.
/// </summary>
public sealed partial class GcNotifyListener : IHostedService, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly GcListenerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GcNotifyListener> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private GcWakeSubscription[] _subscribers = [];
    private Task? _loop;
    private TaskCompletionSource _listening = NewSignal();
    private int _reconnects;
    private int _backendPid;

    public GcNotifyListener(
        NpgsqlDataSource dataSource, GcListenerOptions? options = null, TimeProvider? timeProvider = null, ILogger<GcNotifyListener>? logger = null)
    {
        _dataSource = dataSource;
        _options = options ?? GcListenerOptions.Default;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GcNotifyListener>.Instance;
    }

    /// <summary>How many times the connection was re-established after a loss.</summary>
    public int ReconnectCount => Volatile.Read(ref _reconnects);

    /// <summary>Postgres backend pid of the current listening connection, or 0 while not connected. For tests and diagnostics.</summary>
    public int BackendProcessId => Volatile.Read(ref _backendPid);

    /// <summary>Time source consumers should use for their own waits.</summary>
    public TimeProvider Time => _time;

    /// <summary>Registers a consumer, optionally only for one GC. Dispose it to stop.</summary>
    public GcWakeSubscription Subscribe(Guid? gcId = null)
    {
        var sub = new GcWakeSubscription(this, gcId);
        lock (_gate)
            _subscribers = [.. _subscribers, sub];
        return sub;
    }

    internal void Unsubscribe(GcWakeSubscription sub)
    {
        lock (_gate)
            _subscribers = [.. _subscribers.Where(s => s != sub)];
    }

    /// <summary>Starts the listener loop. Idempotent.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Completes when <c>LISTEN</c> is active; after a loss it completes again once re-established.</summary>
    public Task WaitUntilListeningAsync(CancellationToken ct)
    {
        lock (_gate)
            return _listening.Task.WaitAsync(ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_gate)
            loop = _loop;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = _options.ReconnectMin;
        var everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                conn.Notification += (_, e) => OnNotification(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {GcNotifyChannel.Name}", conn))
                    await listen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                Volatile.Write(ref _backendPid, conn.ProcessID);
                if (everConnected)
                    Interlocked.Increment(ref _reconnects);
                everConnected = true;
                delay = _options.ReconnectMin;
                // LISTEN is active, so anything committed from here on is hinted. Everything before it is found by a read.
                ResyncAll();
                MarkListening();

                await HoldAsync(conn, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException or ObjectDisposedException)
            {
                LogConnectionLost(_logger, delay, ex);
            }

            Volatile.Write(ref _backendPid, 0);
            ResetListening();
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
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

    private void OnNotification(string payload)
    {
        if (!GcNotifyChannel.TryParsePayload(payload, out var gc, out var seq))
        {
            LogBadPayload(_logger);
            return;
        }

        foreach (var sub in Volatile.Read(ref _subscribers))
            sub.Publish(gc, seq);
    }

    private void ResyncAll()
    {
        foreach (var sub in Volatile.Read(ref _subscribers))
            sub.PublishResync();
    }

    private void MarkListening()
    {
        lock (_gate)
            _listening.TrySetResult();
    }

    private void ResetListening()
    {
        lock (_gate)
        {
            if (_listening.Task.IsCompleted)
                _listening = NewSignal();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GC listener connection lost; reconnecting (next wait {Delay}).")]
    private static partial void LogConnectionLost(ILogger logger, TimeSpan delay, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a GC notification with an unreadable payload.")]
    private static partial void LogBadPayload(ILogger logger);
}
