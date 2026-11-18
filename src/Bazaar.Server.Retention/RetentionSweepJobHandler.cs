using Bazaar.Server.Jobs;

namespace Bazaar.Server.Retention;

/// <summary>Runs one retention sweep over every registered category. Idempotent: a re-run after a lost lease finds nothing more to purge.</summary>
public sealed class RetentionSweepJobHandler(RetentionSweeper sweeper) : IJobHandler
{
    public const string JobKind = "retention.sweep";

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct) =>
        _ = await sweeper.SweepAsync(ct).ConfigureAwait(false);
}
