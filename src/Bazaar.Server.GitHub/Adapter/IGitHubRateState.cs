using System.Collections.Concurrent;

namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// Last-seen core REST rate state per installation (doc 21, <c>github_rate_state</c>). The in-memory store below is
/// process-local; the Postgres-backed store comes with the component's schema work package.
/// </summary>
public interface IGitHubRateState
{
    Task<GitHubRateSnapshot?> GetAsync(long installationId, CancellationToken cancellationToken);

    Task SetAsync(long installationId, GitHubRateSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Core limit window as GitHub reported it: <paramref name="Remaining"/> of <paramref name="Limit"/> until <paramref name="ResetAt"/>.</summary>
public sealed record GitHubRateSnapshot(int Limit, int Remaining, DateTimeOffset ResetAt)
{
    /// <summary>Share of the window already used, from 0 to 1.</summary>
    public double UsageRatio => Limit <= 0 ? 1 : (double)(Limit - Math.Clamp(Remaining, 0, Limit)) / Limit;
}

/// <summary>Process-local <see cref="IGitHubRateState"/>. State is lost on restart, which only means one extra probe.</summary>
public sealed class InMemoryGitHubRateState : IGitHubRateState
{
    private readonly ConcurrentDictionary<long, GitHubRateSnapshot> _states = new();

    public Task<GitHubRateSnapshot?> GetAsync(long installationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_states.TryGetValue(installationId, out var snapshot) ? snapshot : null);
    }

    public Task SetAsync(long installationId, GitHubRateSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states[installationId] = snapshot;
        return Task.CompletedTask;
    }
}
