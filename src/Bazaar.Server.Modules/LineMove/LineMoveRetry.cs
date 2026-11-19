namespace Bazaar.Server.Modules.LineMove;

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
