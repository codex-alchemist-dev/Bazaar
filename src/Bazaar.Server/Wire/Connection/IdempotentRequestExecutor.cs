using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Connection;

/// <summary>
/// Idempotent request layer (doc 02 WP7). A request id is the idempotency key, scoped to the device: a retry with
/// the same id gets the original reply and never runs the handler twice, and a retry that arrives while the first
/// is still running waits for it. Server faults (<c>internal</c>) and cancellations are not remembered, so the
/// client may retry them. Ids are kept for <c>wire.request_id_retention_h</c>.
/// </summary>
public sealed class IdempotentRequestExecutor
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(string Device, string Id), Entry> _entries = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private long _lastSweepTicks;

    /// <summary>Creates the executor.</summary>
    public IdempotentRequestExecutor(IOptions<WireOptions> options, TimeProvider time)
    {
        _time = time;
        _retention = TimeSpan.FromHours(options.Value.RequestIdRetentionH);
        _lastSweepTicks = time.GetUtcNow().UtcTicks;
    }

    /// <summary>Number of ids currently remembered (for tests and the status page).</summary>
    public int Count => _entries.Count;

    /// <summary>Runs <paramref name="execute"/> once per (device, id) and returns its reply for every call.</summary>
    public async ValueTask<WireReply> ExecuteAsync(
        string deviceId,
        string requestId,
        Func<CancellationToken, ValueTask<WireReply>> execute,
        CancellationToken cancellationToken)
    {
        Sweep();
        var key = (deviceId, requestId);
        while (true)
        {
            var created = new Entry();
            var entry = _entries.GetOrAdd(key, created);
            if (!ReferenceEquals(entry, created))
            {
                try
                {
                    return await entry.Reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The first attempt was cancelled and forgotten; take over.
                    continue;
                }
            }

            try
            {
                var reply = await execute(cancellationToken).ConfigureAwait(false);
                if (reply.IsRetryable)
                {
                    Forget(key, entry);
                    entry.Reply.TrySetResult(reply);
                }
                else
                {
                    entry.CompletedAtTicks = _time.GetUtcNow().UtcTicks;
                    entry.Reply.TrySetResult(reply);
                }

                return reply;
            }
            catch (OperationCanceledException)
            {
                Forget(key, entry);
                entry.Reply.TrySetCanceled(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                Forget(key, entry);
                entry.Reply.TrySetException(ex);
                throw;
            }
        }
    }

    private void Forget((string, string) key, Entry entry) =>
        _entries.TryRemove(new KeyValuePair<(string, string), Entry>(key, entry));

    private void Sweep()
    {
        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < SweepEvery.Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last)
        {
            return;
        }

        var cutoff = now - _retention.Ticks;
        foreach (var pair in _entries)
        {
            var completed = Interlocked.Read(ref pair.Value.CompletedAtTicks);
            if (completed != 0 && completed < cutoff)
            {
                Forget(pair.Key, pair.Value);
            }
        }
    }

    private sealed class Entry
    {
        public readonly TaskCompletionSource<WireReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long CompletedAtTicks;
    }
}
