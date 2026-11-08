namespace Bazaar.Server.Jobs;

/// <summary>
/// Adds each registered recurring job's bucket to the job queue. Called by <see cref="JobPoller"/> before every poll, so the
/// current bucket's job is in place whenever a poll runs. Adding is idempotent: the dedupe key makes a repeat a no-op.
/// </summary>
public sealed class RecurringJobScheduler
{
    private readonly JobStore _store;
    private readonly IReadOnlyList<RecurringJob> _jobs;

    public RecurringJobScheduler(JobStore store, IEnumerable<RecurringJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(jobs);
        _store = store;
        _jobs = jobs.ToList();
        var duplicate = _jobs.GroupBy(j => j.Kind, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"More than one recurring job is registered for kind '{duplicate.Key}'.", nameof(jobs));
    }

    /// <summary>The registered recurring jobs.</summary>
    public IReadOnlyList<RecurringJob> Jobs => _jobs;

    /// <summary>Adds the current bucket's job for each registered kind. Returns how many were newly added.</summary>
    public async Task<int> EnqueueDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var added = 0;
        foreach (var job in _jobs)
        {
            var planned = RecurringJobPlan.Plan(job, now);
            var id = await _store.EnqueueAsync(planned.Kind, planned.PayloadJson, planned.RunAt, planned.DedupeKey, ct).ConfigureAwait(false);
            if (id is not null)
                added++;
        }

        return added;
    }
}
