namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// How a rolled-back line move is retried (owner decision 2026-11-15). After an automatic rollback the move runs again
/// from Preflight, with exponential backoff between attempts. The defaults are PLANNED operator settings, not decided values.
/// </summary>
/// <param name="MaxAttempts">Total attempts, including the first. When every attempt rolls back, the move halts for the owner.</param>
/// <param name="InitialBackoff">Wait before the second attempt.</param>
/// <param name="Multiplier">Growth of the wait between attempts. Each wait is the previous one times this value.</param>
/// <param name="MaxBackoff">Upper bound on any single wait.</param>
public sealed record LineMoveRetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, double Multiplier, TimeSpan MaxBackoff)
{
    public const int DefaultMaxAttempts = 3;

    /// <summary>Three attempts, waiting 10 minutes and then 20 minutes, never more than an hour.</summary>
    public static LineMoveRetryPolicy Default { get; } = new(DefaultMaxAttempts, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromHours(1));

    /// <summary>Checks the policy's ranges. Throws <see cref="ArgumentOutOfRangeException"/> for any value outside them.</summary>
    public LineMoveRetryPolicy Validated()
    {
        if (MaxAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "MaxAttempts must be from 1 to 10.");
        if (InitialBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialBackoff), InitialBackoff, "InitialBackoff cannot be negative.");
        if (!double.IsFinite(Multiplier) || Multiplier is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(Multiplier), Multiplier, "Multiplier must be from 1 to 10.");
        if (MaxBackoff < InitialBackoff)
            throw new ArgumentOutOfRangeException(nameof(MaxBackoff), MaxBackoff, "MaxBackoff cannot be shorter than InitialBackoff.");
        return this;
    }

    /// <summary>The wait after failed attempt <paramref name="attempt"/> (1-based), before the next one. Capped at <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BackoffAfter(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempts are numbered from 1.");

        var milliseconds = InitialBackoff.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxBackoff.TotalMilliseconds));
    }
}
