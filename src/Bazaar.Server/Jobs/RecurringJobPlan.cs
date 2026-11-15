namespace Bazaar.Server.Jobs;

/// <summary>
/// The pure part of the recurring-job seam: which bucket a moment falls in, and the job that bucket adds. No database here, so
/// the bucket rules are tested directly.
/// </summary>
public static class RecurringJobPlan
{
    /// <summary>One job to add: its kind, payload, due time and dedupe key.</summary>
    public sealed record PlannedJob(string Kind, string PayloadJson, DateTimeOffset RunAt, string DedupeKey);

    /// <summary>
    /// The job for the bucket that contains <paramref name="now"/>. Buckets start at the Unix epoch and are <c>Interval</c> wide.
    /// The due time is the bucket start, so a job for a bucket that has already begun is due at once. The dedupe key has the
    /// kind, the interval and the bucket start, so a changed interval never reuses a key from a different cadence.
    /// </summary>
    public static PlannedJob Plan(RecurringJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        var width = (long)job.Interval.TotalSeconds;
        var bucketStart = now.ToUnixTimeSeconds() / width * width;
        var key = $"recurring:{job.Kind}:{width}:{bucketStart}";
        return new PlannedJob(job.Kind, job.PayloadJson, DateTimeOffset.FromUnixTimeSeconds(bucketStart), key);
    }
}
