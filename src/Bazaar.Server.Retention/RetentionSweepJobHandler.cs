using Bazaar.Server.Jobs;

namespace Bazaar.Server.Retention;

/// <summary>The <c>retention.sweep</c> job (doc 48 section 9). Idempotent: a re-run after an expired lease finds nothing new to do.</summary>
public sealed class RetentionSweepJobHandler(RetentionSweeper sweeper) : IJobHandler
{
    /// <summary>The <c>job.kind</c> the poller enqueues hourly.</summary>
    public const string JobKind = "retention.sweep";

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await sweeper.SweepAsync(ct).ConfigureAwait(false);
    }
}
