namespace Bazaar.Server.Jobs;

/// <summary>
/// A job the poller enqueues on a fixed cadence (doc 03, "Background jobs"). Registering one is how a component schedules a
/// recurring job: the poller adds one job per <see cref="Interval"/> bucket, and <see cref="RecurringJobPlan"/> gives each bucket
/// a dedupe key, so repeated polls and several hosts add one job per bucket, and a missed poll costs one job, not a burst.
/// </summary>
public sealed class RecurringJob
{
    /// <summary>The shortest cadence. Shorter intervals would flood the job table.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>Creates a recurring job.</summary>
    /// <param name="kind">The <c>job.kind</c> to enqueue. Its handler must be registered as an <see cref="IJobHandler"/>.</param>
    /// <param name="interval">The cadence. Whole seconds, at least one second.</param>
    /// <param name="payloadJson">The JSON payload given to every run.</param>
    public RecurringJob(string kind, TimeSpan interval, string payloadJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (interval < MinInterval)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "A recurring job runs at most once a second.");
        if (interval.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "A recurring job interval must be a whole number of seconds.");

        Kind = kind;
        Interval = interval;
        PayloadJson = payloadJson;
    }

    /// <summary>The <c>job.kind</c> to enqueue.</summary>
    public string Kind { get; }

    /// <summary>The cadence.</summary>
    public TimeSpan Interval { get; }

    /// <summary>The JSON payload given to every run.</summary>
    public string PayloadJson { get; }
}
