using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>
/// Postgres <see cref="ILastWriterWinsState"/>: the current value per key in <c>offline_lww_current</c>, every non-current value in
/// <c>offline_lww_history</c>. The key is the write's target only, so Constellation node ids and preference keys share one key space.
/// </summary>
public sealed class PostgresLastWriterWinsState : ILastWriterWinsState
{
    private const string _get =
        "SELECT write_id, hlc_wall, hlc_counter, hlc_device, version, payload FROM offline_lww_current WHERE key = $1";
    private const string _archive =
        "INSERT INTO offline_lww_history (key, write_id, hlc_wall, hlc_counter, hlc_device, version, payload) " +
        "SELECT key, write_id, hlc_wall, hlc_counter, hlc_device, version, payload FROM offline_lww_current WHERE key = $1";
    private const string _upsert =
        "INSERT INTO offline_lww_current (key, write_id, hlc_wall, hlc_counter, hlc_device, version, payload) VALUES ($1, $2, $3, $4, $5, $6, $7) " +
        "ON CONFLICT (key) DO UPDATE SET write_id = EXCLUDED.write_id, hlc_wall = EXCLUDED.hlc_wall, hlc_counter = EXCLUDED.hlc_counter, " +
        "hlc_device = EXCLUDED.hlc_device, version = EXCLUDED.version, payload = EXCLUDED.payload";
    private const string _history =
        "INSERT INTO offline_lww_history (key, write_id, hlc_wall, hlc_counter, hlc_device, version, payload) VALUES ($1, $2, $3, $4, $5, $6, $7)";

    private readonly PostgresAccess _db;

    /// <summary>Creates the state over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresLastWriterWinsState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken) =>
        PostgresClaims.TryClaimAsync(_db, writeId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<LwwEntry?> GetCurrentAsync(string key, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_get, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(key);
            var reader = await statement.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var hlc = new Hlc(reader.GetInt64(1), reader.GetInt32(2), reader.GetGuid(3));
                return new LwwEntry(reader.GetGuid(0), hlc, reader.GetString(4), reader.GetFieldValue<byte[]>(5));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(string key, LwwEntry entry, bool becomesCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (becomesCurrent)
        {
            var archive = await _db.PrepareAsync(_archive, requireScope: true, cancellationToken).ConfigureAwait(false);
            await using (archive.ConfigureAwait(false))
            {
                archive.Add(key);
                await archive.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var statement = await _db.PrepareAsync(becomesCurrent ? _upsert : _history, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(key).Add(entry.WriteId).Add(entry.Hlc.WallMillis).Add(entry.Hlc.Counter).Add(entry.Hlc.DeviceId)
                .Add(entry.Version).Add(entry.Payload.ToArray());
            await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Postgres <see cref="IAttachmentState"/>: content-addressed bytes. One idempotent statement, so no scope is required.</summary>
public sealed class PostgresAttachmentState : IAttachmentState
{
    private const string _store =
        "INSERT INTO offline_attachment (content_hash, payload) VALUES ($1, $2) ON CONFLICT (content_hash) DO NOTHING";

    private readonly PostgresAccess _db;

    /// <summary>Creates the state over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresAttachmentState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public async ValueTask<bool> StoreIfAbsentAsync(string contentHash, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var statement = await _db.PrepareAsync(_store, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(contentHash).Add(payload.ToArray());
            return await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }
}

/// <summary>
/// Postgres <see cref="IAuditActionSink"/>: dedupes on the write id through the shared claim table and gives each action a gapless
/// arrival position from one counter row, so positions follow commit order.
/// </summary>
public sealed class PostgresAuditActionSink : IAuditActionSink
{
    private const string _next = "UPDATE offline_audit_head SET last_pos = last_pos + 1 WHERE id = 1 RETURNING last_pos";
    private const string _append = "INSERT INTO offline_audit_action (position, write_id, payload) VALUES ($1, $2, $3)";

    private readonly PostgresAccess _db;

    /// <summary>Creates the sink over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresAuditActionSink(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public async ValueTask<long?> TryAppendAsync(Guid writeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!await PostgresClaims.TryClaimAsync(_db, writeId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long position;
        var next = await _db.PrepareAsync(_next, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (next.ConfigureAwait(false))
        {
            position = (long)(await next.Command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var append = await _db.PrepareAsync(_append, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (append.ConfigureAwait(false))
        {
            append.Add(position).Add(writeId).Add(payload.ToArray());
            await append.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return position;
    }
}

/// <summary>Postgres <see cref="IReviewSink"/> over <c>offline_review_item</c>. Needs the write's scope, so a review item never outlives a rolled-back merge.</summary>
public sealed class PostgresReviewSink : IReviewSink
{
    private const string _open =
        "INSERT INTO offline_review_item (kind, target, write_id, base_version, server_version, reason) VALUES ($1, $2, $3, $4, $5, $6) " +
        "ON CONFLICT (write_id, kind) DO NOTHING";

    private readonly PostgresAccess _db;

    /// <summary>Creates the sink over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresReviewSink(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public async ValueTask OpenAsync(ReviewItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var statement = await _db.PrepareAsync(_open, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(item.Kind).Add(item.Target).Add(item.WriteId).Add(item.BaseVersion).Add(item.ServerVersion).Add(item.Reason);
            await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Postgres <see cref="IOfflineMergeAudit"/> over <c>offline_merge_audit</c>. Joins the write's scope when one is open.</summary>
public sealed class PostgresOfflineMergeAudit : IOfflineMergeAudit
{
    private const string _record =
        "INSERT INTO offline_merge_audit (device_id, write_id, data_class, target, base_version, server_version, outcome_kind, reason) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";

    private readonly PostgresAccess _db;

    /// <summary>Creates the audit sink over the shared data source.</summary>
    /// <param name="dataSource">The one shared data source.</param>
    public PostgresOfflineMergeAudit(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _db = new PostgresAccess(dataSource);
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(OfflineWrite write, OfflineWriteOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(outcome);
        var statement = await _db.PrepareAsync(_record, requireScope: false, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(write.DeviceId).Add(write.WriteId).Add((short)write.DataClass).Add(write.Target).Add(write.BaseVersion)
                .Add(outcome.ServerVersion).Add((short)outcome.Kind).Add(outcome.Reason);
            await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
