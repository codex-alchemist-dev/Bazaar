using System.Collections.Concurrent;
using Npgsql;

namespace Bazaar.GitHub.Adapter;

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

/// <summary>
/// Postgres-backed <see cref="IGitHubRateState"/> over <c>github_rate_state</c>, bucket <c>core</c>. The table keeps no limit column,
/// so the limit is restored as the GitHub default of 5,000 (the snapshot is only used for the usage ratio and the reset time).
/// </summary>
public sealed class PgGitHubRateState(NpgsqlDataSource dataSource) : IGitHubRateState
{
    private const string _bucket = "core";
    private const int _defaultLimit = 5000;

    public async Task<GitHubRateSnapshot?> GetAsync(long installationId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT remaining, reset_at FROM github_rate_state WHERE installation_id = $1 AND bucket = $2");
        cmd.Parameters.AddWithValue(installationId);
        cmd.Parameters.AddWithValue(_bucket);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GitHubRateSnapshot(_defaultLimit, reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    public async Task SetAsync(long installationId, GitHubRateSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO github_rate_state (installation_id, bucket, remaining, reset_at) VALUES ($1, $2, $3, $4)
            ON CONFLICT (installation_id, bucket) DO UPDATE SET remaining = EXCLUDED.remaining, reset_at = EXCLUDED.reset_at
            """);
        cmd.Parameters.AddWithValue(installationId);
        cmd.Parameters.AddWithValue(_bucket);
        cmd.Parameters.AddWithValue(Math.Max(0, snapshot.Remaining));
        cmd.Parameters.AddWithValue(snapshot.ResetAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
