using Bazaar.Server.OfflineSync.Rules;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>Postgres <see cref="IGcAppendState"/>: a gapless per-GC sequence from a counter row, then the log row, on the write's transaction.</summary>
public sealed class PostgresGcAppendState : IGcAppendState
{
    private const string _next =
        "INSERT INTO offline_gc_head (gc_id, last_seq) VALUES ($1, 1) " +
        "ON CONFLICT (gc_id) DO UPDATE SET last_seq = offline_gc_head.last_seq + 1 RETURNING last_seq";
    private const string _log = "INSERT INTO offline_gc_log (gc_id, seq, payload) VALUES ($1, $2, $3)";

    private readonly PostgresAccess _db;

    /// <summary>Creates the state over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresGcAppendState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken) =>
        PostgresClaims.TryClaimAsync(_db, writeId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<long> AppendAsync(string gcId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        long seq;
        var next = await _db.PrepareAsync(_next, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (next.ConfigureAwait(false))
        {
            next.Add(gcId);
            seq = (long)(await next.Command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var log = await _db.PrepareAsync(_log, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (log.ConfigureAwait(false))
        {
            log.Add(gcId).Add(seq).Add(payload.ToArray());
            await log.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return seq;
    }
}

/// <summary>Postgres <see cref="IGcEditState"/>: every edit is a row; the highest <c>seq</c> of a message is current.</summary>
public sealed class PostgresGcEditState : IGcEditState
{
    private const string _append =
        "WITH concurrent AS (SELECT EXISTS (SELECT 1 FROM offline_gc_edit WHERE message_id = $1 AND base_version IS NOT DISTINCT FROM $2::text) AS found), " +
        "inserted AS (INSERT INTO offline_gc_edit (message_id, write_id, base_version, payload) VALUES ($1, $3, $2::text, $4) RETURNING seq) " +
        "SELECT inserted.seq, concurrent.found FROM inserted, concurrent";
    private const string _current =
        "SELECT write_id, base_version, seq, payload FROM offline_gc_edit WHERE message_id = $1 ORDER BY seq DESC LIMIT 1";

    private readonly PostgresAccess _db;

    /// <summary>Creates the state over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresGcEditState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken) =>
        PostgresClaims.TryClaimAsync(_db, writeId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<GcEditAppend> AppendVersionAsync(string messageId, string? baseVersion, Guid writeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // The check for an edit with the same base version and the insert are one statement, under the target lock of the write's scope.
        var statement = await _db.PrepareAsync(_append, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(messageId).Add(baseVersion).Add(writeId).Add(payload.ToArray());
            var reader = await statement.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                return new GcEditAppend(reader.GetInt64(0), reader.GetBoolean(1));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<GcEditRow?> GetCurrentAsync(string messageId, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_current, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(messageId);
            var reader = await statement.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new GcEditRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3));
            }
        }
    }
}
