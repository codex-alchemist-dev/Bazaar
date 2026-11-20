using Npgsql;

namespace Bazaar.GitHub.Webhooks;

/// <summary>Postgres <see cref="IGitHubDeliveryStore"/>: one insert, <c>on conflict do nothing</c> on the table's primary key.</summary>
public sealed class PgGitHubDeliveryStore(NpgsqlDataSource dataSource) : IGitHubDeliveryStore
{
    private const string _insert =
        "INSERT INTO github_delivery (source, delivery_id, event, received_at) VALUES ($1, $2, $3, $4) ON CONFLICT (source, delivery_id) DO NOTHING";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_insert);
        command.Parameters.AddWithValue(source);
        command.Parameters.AddWithValue(deliveryId);
        command.Parameters.AddWithValue(eventName);
        command.Parameters.AddWithValue(receivedAt.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
