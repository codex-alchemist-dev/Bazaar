namespace Bazaar.Kodo.Export;

/// <summary>What happened to one delivery attempt.</summary>
public enum KodoWebhookOutcome
{
    /// <summary>The consumer answered with a success status.</summary>
    Delivered,

    /// <summary>A failure that may pass: a network error, a timeout, 408, 429 or a 5xx status.</summary>
    RetryableFailure,

    /// <summary>A failure that will not pass by waiting: a redirect (not followed), or another 4xx status.</summary>
    PermanentFailure,
}

/// <summary>What the planner decided after an attempt.</summary>
public enum KodoWebhookStep
{
    /// <summary>The delivery is finished.</summary>
    Done,

    /// <summary>Try again after the decision's delay.</summary>
    Retry,

    /// <summary>Stop and move the delivery to the dead-letter queue, where a replay action can pick it up.</summary>
    DeadLetter,
}

/// <summary>The planner's decision after one attempt.</summary>
/// <param name="Step">What to do next.</param>
/// <param name="Delay">How long to wait before the retry. Zero unless <paramref name="Step"/> is <see cref="KodoWebhookStep.Retry"/>.</param>
/// <param name="Reason">A fixed code for the decision, for the journal and the dead-letter view.</param>
public sealed record KodoWebhookDecision(KodoWebhookStep Step, TimeSpan Delay, string Reason);

/// <summary>
/// The retry schedule of a webhook delivery (doc 46 section 15.3). Each retry waits an exponentially growing delay, capped, with a random
/// downward jitter. Retries stop once the next one would land after the window (24 hours by default), and the delivery is dead-lettered.
/// The planner does no I/O and reads no clock: the caller passes the elapsed time and the random source.
/// </summary>
/// <remarks>
/// The numbers are PROPOSED (doc 46 section 15.7): the doc says "exponential backoff and jitter, up to 24 hours" and sets no base or cap.
/// </remarks>
public sealed class KodoWebhookRetryPolicy
{
    /// <summary>The shipped schedule: 30 seconds doubling to a 4 hour cap, 20 percent jitter, a 24 hour window.</summary>
    public static KodoWebhookRetryPolicy Defaults { get; } =
        new(TimeSpan.FromSeconds(30), 2, TimeSpan.FromHours(4), 0.2, TimeSpan.FromHours(24));

    /// <summary>Creates a schedule.</summary>
    /// <param name="initialDelay">The nominal delay before the first retry. Positive.</param>
    /// <param name="multiplier">The growth factor per retry. At least 1.</param>
    /// <param name="maxDelay">The cap on a nominal delay. Not below <paramref name="initialDelay"/>.</param>
    /// <param name="jitterFraction">The largest share of a delay that jitter may remove, from 0 (none) up to but not including 1.</param>
    /// <param name="window">The time from the first attempt after which no retry may start. Not below <paramref name="maxDelay"/>.</param>
    public KodoWebhookRetryPolicy(TimeSpan initialDelay, double multiplier, TimeSpan maxDelay, double jitterFraction, TimeSpan window)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), initialDelay, "The initial delay must be positive.");
        }

        if (!double.IsFinite(multiplier) || multiplier < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "The multiplier must be a finite number of at least 1.");
        }

        if (maxDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), maxDelay, "The cap must not be below the initial delay.");
        }

        if (!double.IsFinite(jitterFraction) || jitterFraction < 0 || jitterFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction), jitterFraction, "The jitter must be at least 0 and below 1.");
        }

        if (window < maxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must not be shorter than the delay cap.");
        }

        InitialDelay = initialDelay;
        Multiplier = multiplier;
        MaxDelay = maxDelay;
        JitterFraction = jitterFraction;
        Window = window;
    }

    /// <summary>The nominal delay before the first retry.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>The growth factor per retry.</summary>
    public double Multiplier { get; }

    /// <summary>The cap on a nominal delay.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>The largest share of a delay that jitter may remove.</summary>
    public double JitterFraction { get; }

    /// <summary>The time from the first attempt after which no retry may start.</summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// The nominal (unjittered) delay before retry number <paramref name="failedAttempts"/>, which is the number of attempts that have failed so far.
    /// </summary>
    public TimeSpan NominalDelay(int failedAttempts)
    {
        if (failedAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttempts), failedAttempts, "At least one attempt must have failed.");
        }

        double seconds = InitialDelay.TotalSeconds * Math.Pow(Multiplier, failedAttempts - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    /// <summary>
    /// Decides the next step after one attempt.
    /// </summary>
    /// <param name="outcome">The result of the attempt.</param>
    /// <param name="failedAttempts">How many attempts have failed, including this one. At least 1 for a failure.</param>
    /// <param name="elapsedSinceFirstAttempt">The time from the first attempt to now. Not negative.</param>
    /// <param name="random">The jitter source. Its <see cref="Random.NextDouble"/> must return a value from 0 up to but not including 1.</param>
    public KodoWebhookDecision Decide(KodoWebhookOutcome outcome, int failedAttempts, TimeSpan elapsedSinceFirstAttempt, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        switch (outcome)
        {
            case KodoWebhookOutcome.Delivered:
                return new KodoWebhookDecision(KodoWebhookStep.Done, TimeSpan.Zero, "delivered");
            case KodoWebhookOutcome.PermanentFailure:
                return new KodoWebhookDecision(KodoWebhookStep.DeadLetter, TimeSpan.Zero, "permanent_failure");
            case KodoWebhookOutcome.RetryableFailure:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a delivery outcome.");
        }

        if (elapsedSinceFirstAttempt < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSinceFirstAttempt), elapsedSinceFirstAttempt, "The elapsed time must not be negative.");
        }

        TimeSpan nominal = NominalDelay(failedAttempts);
        double jitter = 1 - (JitterFraction * random.NextDouble());
        TimeSpan delay = TimeSpan.FromSeconds(nominal.TotalSeconds * jitter);

        if (elapsedSinceFirstAttempt + delay > Window)
        {
            return new KodoWebhookDecision(KodoWebhookStep.DeadLetter, TimeSpan.Zero, "schedule_exhausted");
        }

        return new KodoWebhookDecision(KodoWebhookStep.Retry, delay, "retry");
    }

    /// <summary>
    /// The delays of a delivery that fails every time, until the schedule dead-letters it. Used to show the schedule and to test its bounds.
    /// </summary>
    public IReadOnlyList<TimeSpan> PlanDelays(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        List<TimeSpan> delays = [];
        TimeSpan elapsed = TimeSpan.Zero;
        for (int failedAttempts = 1; ; failedAttempts++)
        {
            KodoWebhookDecision decision = Decide(KodoWebhookOutcome.RetryableFailure, failedAttempts, elapsed, random);
            if (decision.Step != KodoWebhookStep.Retry)
            {
                return delays;
            }

            delays.Add(decision.Delay);
            elapsed += decision.Delay;
        }
    }
}

/// <summary>Maps an HTTP status to a delivery outcome (doc 46 section 15.3).</summary>
public static class KodoWebhookOutcomes
{
    /// <summary>The outcome of a response with this status. Redirects are not followed, so they are permanent.</summary>
    /// <param name="statusCode">The HTTP status, from 100 to 599.</param>
    public static KodoWebhookOutcome FromStatusCode(int statusCode)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "An HTTP status is from 100 to 599.");
        }

        return statusCode switch
        {
            >= 200 and <= 299 => KodoWebhookOutcome.Delivered,
            408 or 429 => KodoWebhookOutcome.RetryableFailure,
            >= 500 and <= 599 => KodoWebhookOutcome.RetryableFailure,
            _ => KodoWebhookOutcome.PermanentFailure,
        };
    }
}
