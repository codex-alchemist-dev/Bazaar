using System.Collections.Concurrent;
using Npgsql;

namespace Bazaar.Server.GitHub.Ingress;

/// <summary>
/// Remembers which events were fully processed (doc 21, <c>github_delivery</c>). Webhooks and polls share the table, keyed by
/// (source, delivery id); the poller uses source <see cref="PollSource"/>.
/// </summary>
public interface IGitHubDeliveryStore
{
    public const string PollSource = "poll";

    Task<bool> IsProcessedAsync(string source, string deliveryId, CancellationToken cancellationToken);

    Task MarkProcessedAsync(string source, string deliveryId, string eventName, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Process-local store, used when no database is registered. A restart forgets it, so events inside <c>MaxEventAge</c> may be delivered again.</summary>
public sealed class InMemoryGitHubDeliveryStore : IGitHubDeliveryStore
{
    private const int _capacity = 50_000;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public Task<bool> IsProcessedAsync(string source, string deliveryId, CancellationToken cancellationToken) =>
        Task.FromResult(_seen.ContainsKey(source + "\n" + deliveryId));

    public Task MarkProcessedAsync(string source, string deliveryId, string eventName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_seen.Count >= _capacity)
        {
            // Bounded: dropping the window only risks one repeat delivery, which sinks must tolerate.
            _seen.Clear();
        }

        _seen[source + "\n" + deliveryId] = 0;
        return Task.CompletedTask;
    }
}

/// <summary>Postgres store over <c>github_delivery</c> (raw Npgsql, one shared data source).</summary>
public sealed class PgGitHubDeliveryStore(NpgsqlDataSource dataSource) : IGitHubDeliveryStore
{
    public async Task<bool> IsProcessedAsync(string source, string deliveryId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT 1 FROM github_delivery WHERE source = $1 AND delivery_id = $2 AND processed_at IS NOT NULL");
        cmd.Parameters.AddWithValue(source);
        cmd.Parameters.AddWithValue(deliveryId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task MarkProcessedAsync(string source, string deliveryId, string eventName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO github_delivery (source, delivery_id, event, received_at, processed_at)
            VALUES ($1, $2, $3, $4, $4)
            ON CONFLICT (source, delivery_id) DO UPDATE SET processed_at = COALESCE(github_delivery.processed_at, EXCLUDED.processed_at)
            """);
        cmd.Parameters.AddWithValue(source);
        cmd.Parameters.AddWithValue(deliveryId);
        cmd.Parameters.AddWithValue(eventName);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
