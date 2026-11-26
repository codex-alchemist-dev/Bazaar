using Bazaar.OfflineSync;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>
/// Postgres <see cref="IOfflineSyncStore"/> over <c>offline_write_seen</c> (one row per device and write id, with the outcome) and
/// <c>offline_device_seq</c> (the acked sequence). <see cref="RecordAsync"/> joins the open write scope, so the outcome commits
/// with the state change; with no scope open it writes both rows in its own transaction.
/// </summary>
public sealed class PostgresOfflineSyncStore : IOfflineSyncStore
{
    private const string _acked = "SELECT acked_sequence FROM offline_device_seq WHERE device_id = $1";
    private const string _find =
        "SELECT outcome_kind, server_version, reason FROM offline_write_seen WHERE device_id = $1 AND write_id = $2";
    private const string _seen =
        "INSERT INTO offline_write_seen (device_id, write_id, device_sequence, outcome_kind, server_version, reason) " +
        "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (device_id, write_id) DO NOTHING";
    private const string _advance =
        "INSERT INTO offline_device_seq (device_id, acked_sequence) VALUES ($1, $2) " +
        "ON CONFLICT (device_id) DO UPDATE SET acked_sequence = GREATEST(offline_device_seq.acked_sequence, EXCLUDED.acked_sequence)";

    private readonly PostgresAccess _db;

    /// <summary>Creates the store over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresOfflineSyncStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public async ValueTask<long> GetAckedSequenceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_acked, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(deviceId);
            var value = await statement.Command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is long acked ? acked : 0;
        }
    }

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome?> FindOutcomeAsync(Guid deviceId, Guid writeId, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_find, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(deviceId).Add(writeId);
            var reader = await statement.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new OfflineWriteOutcome(
                    writeId,
                    (OfflineOutcomeKind)reader.GetInt16(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(Guid deviceId, long sequence, OfflineWriteOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (PostgresWriteScope.Current is not null)
        {
            await RecordCoreAsync(deviceId, sequence, outcome, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No write scope (a direct caller): both rows still commit together.
        var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await Run(_seen, connection, transaction, c => c.Add(deviceId).Add(outcome.WriteId).Add(sequence)
                    .Add((short)outcome.Kind).Add(outcome.ServerVersion).Add(outcome.Reason), cancellationToken).ConfigureAwait(false);
                await Run(_advance, connection, transaction, c => c.Add(deviceId).Add(sequence), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask RecordCoreAsync(Guid deviceId, long sequence, OfflineWriteOutcome outcome, CancellationToken cancellationToken)
    {
        var seen = await _db.PrepareAsync(_seen, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (seen.ConfigureAwait(false))
        {
            seen.Add(deviceId).Add(outcome.WriteId).Add(sequence).Add((short)outcome.Kind).Add(outcome.ServerVersion).Add(outcome.Reason);
            await seen.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var advance = await _db.PrepareAsync(_advance, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (advance.ConfigureAwait(false))
        {
            advance.Add(deviceId).Add(sequence);
            await advance.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask Run(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, Action<Statement> bind, CancellationToken cancellationToken)
    {
        var statement = new Statement(new NpgsqlCommand(sql, connection, transaction), null);
        await using (statement.ConfigureAwait(false))
        {
            bind(statement);
            await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
