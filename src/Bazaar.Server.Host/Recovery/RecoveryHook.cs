using Bazaar.Server.Agents.Recovery;

namespace Bazaar.Server.Host.Recovery;

/// <summary>Seam over <see cref="AgentLeaseRecovery"/> so the hook can be tested with a fixed report.</summary>
public interface ILeaseRecoveryRunner
{
    Task<LeaseRecoveryReport> RecoverAsync(CancellationToken ct);
}

/// <summary>Default runner: calls the doc 06 recovery directly. Concurrent passes (this sweep and the <c>agent.lease_recovery</c> job) are serialised in process.</summary>
public sealed class LeaseRecoveryRunner(AgentLeaseRecovery recovery) : ILeaseRecoveryRunner
{
    // AgentLeaseRecovery takes no advisory lock. Its DELETE ... RETURNING row-locks expired leases, so a second concurrent pass would be
    // correct anyway (it blocks on the locked rows, then finds them gone); this gate only avoids the wasted contention.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<LeaseRecoveryReport> RecoverAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await recovery.RecoverExpiredAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>Something that must flush state on graceful stop. Implementations should honour the token; the hook bounds them by the shutdown timeout.</summary>
public interface IShutdownCheckpointParticipant
{
    Task CheckpointAsync(CancellationToken ct);
}

public sealed class RecoveryHookOptions
{
    public const string SectionName = "Bazaar:RecoveryHook";

    /// <summary>Upper bound for the shutdown checkpoint. Default matches <c>host.shutdown_timeout_seconds</c> (20).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>Records whether the startup sweep finished and when the shutdown checkpoint ran. Read by <see cref="RecoverySweepReadinessCheck"/>.</summary>
public sealed class RecoveryHookState
{
    private volatile bool _sweepCompleted;
    private long _checkpointTicks;

    public bool SweepCompleted => _sweepCompleted;

    public LeaseRecoveryReport? LastReport { get; private set; }

    public DateTimeOffset? CheckpointAt => _checkpointTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _checkpointTicks), TimeSpan.Zero);

    internal void SweepDone(LeaseRecoveryReport report)
    {
        LastReport = report;
        _sweepCompleted = true;
    }

    internal void CheckpointDone(DateTimeOffset at) => Interlocked.Exchange(ref _checkpointTicks, at.UtcTicks);
}
