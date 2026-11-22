namespace Bazaar.Server.CodingRunner;

/// <summary>Runner choices. Defaults follow doc 17 and doc 16 (exec maximum 2 h).</summary>
public sealed record CodingToolRunnerOptions
{
    /// <summary>How long a writer call waits for another writer on the same worktree before it returns busy.</summary>
    public TimeSpan LockWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a waiting writer retries the lock.</summary>
    public TimeSpan LockPollInterval { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>The lease a writer holds; doc 16's exec maximum, so no honest call outlives it.</summary>
    public TimeSpan LockLeaseDuration { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Checks the values. Throws on one that would break exclusion or spin.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(LockWaitTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LockPollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LockLeaseDuration, TimeSpan.Zero);
    }
}
