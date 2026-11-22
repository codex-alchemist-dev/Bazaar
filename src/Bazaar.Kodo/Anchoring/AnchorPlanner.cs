namespace Bazaar.Kodo.Anchoring;

/// <summary>Why the planner decided to anchor, or that it did not.</summary>
public enum AnchorPlanReason
{
    /// <summary>Do not anchor now: no new records, or neither threshold reached.</summary>
    NotDue,

    /// <summary>At least <see cref="AnchorPlanOptions.MaxRecords"/> records since the last anchor.</summary>
    RecordCount,

    /// <summary>At least <see cref="AnchorPlanOptions.MaxInterval"/> since the last anchor, and there are new records.</summary>
    Interval,

    /// <summary>Due, but a recent failed attempt is still inside the retry wait.</summary>
    RetryWait,
}

/// <summary>The planner's answer.</summary>
public sealed record AnchorPlan(bool ShouldAnchor, AnchorPlanReason Reason);

/// <summary>The last successful anchor: how many records it covered and when. Start with (0, start time).</summary>
public sealed record AnchorProgress(long Size, DateTimeOffset At);

/// <summary>
/// Planner settings. Defaults are PROPOSED (doc 46 section 6.5.1): 1,000 records, 15 minutes (the checkpoint cadence
/// of section 6.5), and a 1 minute wait after a failed attempt.
/// </summary>
public sealed record AnchorPlanOptions
{
    /// <summary>Upper bound on records between anchors. Range 1 to 10,000,000.</summary>
    public long MaxRecords { get; init; } = 1000;

    /// <summary>Upper bound on time between anchors. Range 1 second to 24 hours.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum wait after a failed attempt before trying again. Range 1 second to 1 hour.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>True when every value is inside its range.</summary>
    public bool IsValid =>
        MaxRecords is >= 1 and <= 10_000_000
        && MaxInterval >= TimeSpan.FromSeconds(1) && MaxInterval <= TimeSpan.FromHours(24)
        && RetryInterval >= TimeSpan.FromSeconds(1) && RetryInterval <= TimeSpan.FromHours(1);
}

/// <summary>
/// Decides when to anchor: every N records or every T of time, whichever comes first, and never when there is
/// nothing new. A failed attempt delays the next try so a dead sink is not hammered. Pure decision logic; the
/// caller keeps the progress and publishes.
/// </summary>
public sealed class AnchorPlanner
{
    private readonly AnchorPlanOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a planner.</summary>
    /// <exception cref="ArgumentException">The options are out of range.</exception>
    public AnchorPlanner(AnchorPlanOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new AnchorPlanOptions();
        if (!_options.IsValid)
        {
            throw new ArgumentException("Anchor plan options are out of range.", nameof(options));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Evaluates the current journal length against the last successful anchor and the last failed attempt, if any.</summary>
    public AnchorPlan Evaluate(long currentSize, AnchorProgress lastAnchored, DateTimeOffset? lastFailedAt = null)
    {
        ArgumentNullException.ThrowIfNull(lastAnchored);

        long newRecords = currentSize - lastAnchored.Size;
        if (newRecords <= 0)
        {
            return new AnchorPlan(false, AnchorPlanReason.NotDue);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        AnchorPlanReason reason;
        if (newRecords >= _options.MaxRecords)
        {
            reason = AnchorPlanReason.RecordCount;
        }
        else if (now - lastAnchored.At >= _options.MaxInterval)
        {
            reason = AnchorPlanReason.Interval;
        }
        else
        {
            return new AnchorPlan(false, AnchorPlanReason.NotDue);
        }

        if (lastFailedAt is { } failed && now - failed < _options.RetryInterval)
        {
            return new AnchorPlan(false, AnchorPlanReason.RetryWait);
        }

        return new AnchorPlan(true, reason);
    }
}
