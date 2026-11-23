using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>
/// <see cref="IGcMessageVersionSource"/> over <c>gc_message_version</c> (docs/design/01, doc 44 section 4.2). Reads by the primary-key
/// prefix <c>gc_id</c>, so a read prunes to one partition, and returns ciphertext only: the body column is never decrypted here.
/// The viewer's grant is asked for first, as <c>GcLogReader</c> does, and bounds the read at the grant's last seq.
/// </summary>
public sealed class PostgresGcMessageVersionSource(NpgsqlDataSource dataSource, IGcReadAccess access) : IGcMessageVersionSource
{
    // A seq belongs to one event, and an event writes one version, so seq alone orders the rows; message_id and version break a tie
    // only if a future writer ever puts two versions on one seq.
    private const string _readSql = """
        SELECT seq, message_id, version, editor_id, created_at, tombstone, key_id, body_nonce, body_ct
        FROM gc_message_version
        WHERE gc_id = $1 AND seq > $2 AND seq <= $3
        ORDER BY seq, message_id, version
        LIMIT $4
        """;

    /// <inheritdoc />
    public async IAsyncEnumerable<GcMessageVersionRecord> ReadAsync(
        Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeq);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var grant = await access.GetGrantAsync(gcId, viewer, ct).ConfigureAwait(false);
        if (!grant.Allowed)
        {
            throw new GcAccessDeniedException(gcId);
        }

        await using var cmd = dataSource.CreateCommand(_readSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterSeq });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = grant.ThroughSeq ?? long.MaxValue });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new GcMessageVersionRecord(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8));
        }
    }
}
