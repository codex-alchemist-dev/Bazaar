using System.Text.Json.Nodes;
using System.Threading.Channels;
using Bazaar.Server.Security.Audit;

namespace Bazaar.ModulePermissions.Audit;

/// <summary>
/// Writes refused module accesses to the audit chain as <c>module.permission.denied</c> (doc 48 section 1). The gate calls
/// <see cref="Record"/> on the module's call path, so it only queues; one background pump appends in order. The queue is
/// bounded: when the audit store is slow or down, new refusals are counted instead of held, and the count is written with
/// the next entry (<c>not_recorded_before</c>). A failed append is retried with a growing delay and is never skipped while the
/// sink runs. The refusal itself never depends on this sink: the gate has already refused.
/// </summary>
public sealed class AuditLogModulePermissionDenialSink : IModulePermissionDenialSink, IAsyncDisposable
{
    /// <summary>Refusals held while the audit store catches up.</summary>
    public const int QueueCapacity = 1024;

    /// <summary>The longest wait between two tries of one append.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan _firstRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly IAuditLog _log;
    private readonly TimeProvider _time;
    private readonly Channel<ModulePermissionDenial> _queue = Channel.CreateBounded<ModulePermissionDenial>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private long _overflow;
    private long _written;
    private int _disposed;

    public AuditLogModulePermissionDenialSink(IAuditLog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _log = log;
        _time = time;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Entries appended to the audit log so far.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Refusals counted but not yet reported, because the queue was full.</summary>
    public long PendingOverflow => Interlocked.Read(ref _overflow);

    /// <inheritdoc />
    public void Record(ModulePermissionDenial denial)
    {
        ArgumentNullException.ThrowIfNull(denial);
        if (!_queue.Writer.TryWrite(denial))
        {
            Interlocked.Increment(ref _overflow);
        }
    }

    /// <summary>Stops taking refusals, writes what is queued (one try each) and stops the pump.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>The audit payload of one refusal. Keys are fixed; every value is a value the gate already vetted.</summary>
    private static string Payload(ModulePermissionDenial denial, long notRecordedBefore) =>
        new JsonObject
        {
            ["class"] = denial.Class,
            ["decision"] = DecisionName(denial.Decision),
            ["module_id"] = denial.ModuleId,
            ["not_recorded_before"] = denial.SuppressedBefore + notRecordedBefore,
            ["target"] = denial.Target,
            ["target_length"] = denial.TargetLength,
        }.ToJsonString();

    /// <summary>Wire name of a decision; explicit so the audit record does not depend on enum member names.</summary>
    private static string DecisionName(ModuleAccessDecision decision) => decision switch
    {
        ModuleAccessDecision.DeniedNotGranted => "not_granted",
        ModuleAccessDecision.DeniedInvalidTarget => "invalid_target",
        ModuleAccessDecision.DeniedUnknownClass => "unknown_class",
        ModuleAccessDecision.DeniedModuleNotRunning => "module_not_running",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Not a refusal."),
    };

    private async Task PumpAsync()
    {
        var reader = _queue.Reader;
        while (true)
        {
            try
            {
                if (!await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping: fall through and drain what is already queued, one try each.
            }

            while (reader.TryRead(out var denial))
            {
                await AppendAsync(denial).ConfigureAwait(false);
            }

            if (_stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task AppendAsync(ModulePermissionDenial denial)
    {
        var overflow = Interlocked.Exchange(ref _overflow, 0);
        var entry = new AuditEntry(denial.At, $"module:{denial.ModuleId}", ModulePermissionDenial.AuditKind, Payload(denial, overflow));
        var delay = _firstRetryDelay;
        while (true)
        {
            try
            {
                // Not cancelled by the stop token: an append that has started is allowed to finish.
                await _log.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _written);
                return;
            }
#pragma warning disable CA1031 // Any audit store failure is retried; the kind of failure does not change that.
            catch (Exception)
#pragma warning restore CA1031
            {
                if (_stop.IsCancellationRequested)
                {
                    // Shutting down with the store still failing: keep the count so a caller can see what was lost.
                    Interlocked.Add(ref _overflow, overflow + 1);
                    return;
                }
            }

            try
            {
                await Task.Delay(delay, _time, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping: one last try above, then give up.
            }

            delay = delay + delay > MaxRetryDelay ? MaxRetryDelay : delay + delay;
        }
    }
}
