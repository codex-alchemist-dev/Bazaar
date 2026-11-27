using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.GitHub.Webhooks;

/// <summary>Postgres <see cref="IGitHubDeliveryStore"/>: one insert, <c>on conflict do nothing</c> on the table's primary key.</summary>
public sealed class PgGitHubDeliveryStore(NpgsqlDataSource dataSource) : IGitHubDeliveryStore
{
    private const string _insert =
        "INSERT INTO github_delivery (source, delivery_id, event, received_at, payload, event_type) VALUES ($1, $2, $3, $4, $5, $3) ON CONFLICT (source, delivery_id) DO NOTHING";

    // jsonb rejects the escape \u0000 (SQLSTATE 22P05); the body is then kept out of the row rather than failing the delivery.
    private const string _unsupportedEscape = "22P05";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, CancellationToken cancellationToken) =>
        TryRecordAsync(source, deliveryId, eventName, receivedAt, null, cancellationToken);

    public async Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, string? payloadJson, CancellationToken cancellationToken)
    {
        try
        {
            return await InsertAsync(source, deliveryId, eventName, receivedAt, payloadJson, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (payloadJson is not null && ex.SqlState == _unsupportedEscape)
        {
            return await InsertAsync(source, deliveryId, eventName, receivedAt, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> InsertAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, string? payloadJson, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_insert);
        command.Parameters.AddWithValue(source);
        command.Parameters.AddWithValue(deliveryId);
        command.Parameters.AddWithValue(eventName);
        command.Parameters.AddWithValue(receivedAt.UtcDateTime);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)payloadJson ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
