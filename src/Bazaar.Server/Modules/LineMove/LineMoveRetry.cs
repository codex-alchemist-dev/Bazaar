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

/// <summary>
/// The result of a retried move. <see cref="Halted"/> is true when every attempt rolled back, so the move stops and the
/// owner (and Kodo) must decide. Any other status is final for that move and is not retried.
/// </summary>
/// <param name="Last">The outcome of the last attempt.</param>
/// <param name="Attempts">How many attempts ran.</param>
/// <param name="Halted">True when the retries were used up with the move still rolled back.</param>
public sealed record LineMoveRetryOutcome(LineMoveOutcome Last, int Attempts, bool Halted);

/// <summary>
/// Runs a line move and retries it after each automatic rollback, within the policy's bounds. Only
/// <see cref="LineMoveStatus.RolledBack"/> is retried: the live state is back on the old line, so another try is safe.
/// Failed, manual-rollback, cancelled and completed runs end at once. Cancellation during a wait throws, and the
/// rolled-back state stays on the old line.
/// </summary>
public sealed class LineMoveRetryRunner
{
    private readonly LineMoveRunner _runner;
    private readonly LineMoveRetryPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public LineMoveRetryRunner(LineMoveRunner runner, LineMoveRetryPolicy policy, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(policy);
        _runner = runner;
        _policy = policy.Validated();
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    public async Task<LineMoveRetryOutcome> RunAsync(LineMoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await _runner.RunAsync(request, cancellationToken);
            if (outcome.Status != LineMoveStatus.RolledBack)
                return new LineMoveRetryOutcome(outcome, attempt, Halted: false);

            if (attempt >= _policy.MaxAttempts)
                return new LineMoveRetryOutcome(outcome, attempt, Halted: true);

            await _delay(_policy.BackoffAfter(attempt), cancellationToken);
        }
    }
}
