using Npgsql;

namespace Bazaar.GitHub.Processing;

/// <summary>One claimed <c>github_delivery</c> row.</summary>
/// <param name="Attempts">The attempt count including this claim (1 on the first try).</param>
public sealed record ClaimedDelivery(string Source, string DeliveryId, string EventType, string? PayloadJson, DateTimeOffset ReceivedAt, int Attempts);

/// <summary>The unprocessed-delivery queue over <c>github_delivery</c> (doc 21). Safe for several server instances.</summary>
public interface IGitHubDeliveryInbox
{
    /// <summary>
    /// Claims up to <paramref name="limit"/> unprocessed webhook rows that are due, oldest first. A claimed row is invisible to other
    /// claimers until <paramref name="lease"/> passes, so a crashed instance's rows come back.
    /// </summary>
    Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int limit, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>The sink accepted the delivery (or it was an ignored event type): sets <c>processed_at</c>.</summary>
    Task CompleteAsync(string source, string deliveryId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The sink failed: the row stays unprocessed and is claimable again at <paramref name="retryAt"/>.</summary>
    Task RetryLaterAsync(string source, string deliveryId, DateTimeOffset retryAt, CancellationToken cancellationToken);

    /// <summary>The row can never be processed: sets <c>processed_at</c> and records <paramref name="reason"/>.</summary>
    Task PoisonAsync(string source, string deliveryId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
}

/// <summary>Postgres <see cref="IGitHubDeliveryInbox"/>: raw Npgsql, claims with <c>FOR UPDATE SKIP LOCKED</c>.</summary>
public sealed class PgGitHubDeliveryInbox(NpgsqlDataSource dataSource, string source = Webhooks.GitHubWebhookEndpoint.Source) : IGitHubDeliveryInbox
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int limit, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        // One statement: the sub-select locks the due rows and skips rows another instance holds, the update leases them.
        await using var cmd = _dataSource.CreateCommand(
            """
            UPDATE github_delivery d
               SET process_attempts = d.process_attempts + 1, next_attempt_at = $3
              FROM (SELECT source, delivery_id FROM github_delivery
                     WHERE source = $1 AND processed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= $2)
                     ORDER BY received_at, delivery_id
                     LIMIT $4
                       FOR UPDATE SKIP LOCKED) due
             WHERE d.source = due.source AND d.delivery_id = due.delivery_id
            RETURNING d.source, d.delivery_id, COALESCE(d.event_type, d.event), d.payload::text, d.received_at, d.process_attempts
            """);
        cmd.Parameters.AddWithValue(source);
        cmd.Parameters.AddWithValue(now.UtcDateTime);
        cmd.Parameters.AddWithValue((now + lease).UtcDateTime);
        cmd.Parameters.AddWithValue(limit);

        var claimed = new List<ClaimedDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new ClaimedDelivery(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.GetInt32(5)));
        }

        claimed.Sort(static (a, b) => a.ReceivedAt.CompareTo(b.ReceivedAt));
        return claimed;
    }

    public Task CompleteAsync(string source, string deliveryId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE github_delivery SET processed_at = $3, next_attempt_at = NULL WHERE source = $1 AND delivery_id = $2 AND processed_at IS NULL",
            source, deliveryId, now.UtcDateTime, cancellationToken);

    public Task RetryLaterAsync(string source, string deliveryId, DateTimeOffset retryAt, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE github_delivery SET next_attempt_at = $3 WHERE source = $1 AND delivery_id = $2 AND processed_at IS NULL",
            source, deliveryId, retryAt.UtcDateTime, cancellationToken);

    public Task PoisonAsync(string source, string deliveryId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE github_delivery SET processed_at = $3, next_attempt_at = NULL, process_error = $4 WHERE source = $1 AND delivery_id = $2 AND processed_at IS NULL",
            source, deliveryId, now.UtcDateTime, cancellationToken, reason);

    private async Task ExecuteAsync(string sql, string source, string deliveryId, DateTime time, CancellationToken cancellationToken, string? reason = null)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(source);
        cmd.Parameters.AddWithValue(deliveryId);
        cmd.Parameters.AddWithValue(time);
        if (reason is not null)
        {
            cmd.Parameters.AddWithValue(reason.Length <= 500 ? reason : reason[..500]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
