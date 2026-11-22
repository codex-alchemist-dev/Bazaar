namespace Bazaar.Server.Git.GitLab.Tests.Support;

/// <summary>Issues a fresh credential per lease and records each lease, so tests can check the operation and the disposal.</summary>
internal sealed class FakeCredentialSource : IGitCredentialSource
{

    public const string Secret = "fake-gitlab-token-not-a-secret";

    private readonly List<(RepoRef Repo, GitOperation Operation, GitCredential Credential)> _leases = [];

    public IReadOnlyList<(RepoRef Repo, GitOperation Operation, GitCredential Credential)> Leases
    {
        get
        {
            lock (_leases)
            {
                return _leases.ToList();
            }
        }
    }

    public Task<GitCredential> LeaseAsync(RepoRef repo, GitOperation operation, CancellationToken cancellationToken)
    {
        var credential = new GitCredential("bazaar-agent", Secret, DateTimeOffset.UnixEpoch.AddDays(1));
        lock (_leases)
        {
            _leases.Add((repo, operation, credential));
        }

        return Task.FromResult(credential);
    }
}

/// <summary>A time provider whose timers fire at once and record the delay asked for, so retry waits are asserted without sleeping.</summary>
internal sealed class InstantTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_delays)
            {
                return _delays.ToList();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_delays)
        {
            _delays.Add(dueTime);
        }

        _ = Task.Run(() => callback(state));
        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
