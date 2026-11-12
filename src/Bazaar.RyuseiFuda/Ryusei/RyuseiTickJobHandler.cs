using Bazaar.Server.Jobs;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// Runs the <c>ryusei.trigger_tick</c> job (doc 38, WP-R5) by running one <see cref="RyuseiTickService.TickAsync"/> pass.
/// The job is idempotent: a re-run after a lost lease finds the fires it already wrote and skips them through the unique key.
/// The recurring enqueue of this job belongs to the scheduler seam and is not here yet, as with <c>kanban.deadline_scan</c>.
/// </summary>
public sealed class RyuseiTickJobHandler(RyuseiTickService tick) : IJobHandler
{
    /// <summary>The job kind served by this handler.</summary>
    public const string JobKind = "ryusei.trigger_tick";

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct) => await tick.TickAsync(ct).ConfigureAwait(false);
}
