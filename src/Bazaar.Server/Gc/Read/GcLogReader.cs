using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Npgsql;

namespace Bazaar.Server.Gc.Read;

/// <summary>Reads <c>gc_event</c> by <c>(gc_id, seq)</c>, the primary key, so a read prunes to one partition.</summary>
public sealed class GcLogReader(NpgsqlDataSource dataSource, IGcReadAccess access) : IGcLogReader
{
    private const string _readSql = """
        SELECT seq, event_id, kind, author_kind, author_id, source_trust, tainted_at_write, taint_sources::text,
               message_id, version, target_seq, reply_to_seq, payload::text, created_at
        FROM gc_event
        WHERE gc_id = $1 AND seq > $2 AND seq <= $3
        ORDER BY seq
        LIMIT $4
        """;

    /// <inheritdoc />
    public async IAsyncEnumerable<GcEvent> ReadAsync(
        Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSeq);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IGcLogReader.MaxLimit);

        var grant = await access.GetGrantAsync(gcId, viewer, ct).ConfigureAwait(false);
        if (!grant.Allowed)
            throw new GcAccessDeniedException(gcId);

        await using var cmd = dataSource.CreateCommand(_readSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterSeq });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = grant.ThroughSeq ?? long.MaxValue });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new GcEvent(
                gcId,
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                GcEnumText.ParseAuthorKind(reader.GetString(3)),
                reader.GetGuid(4),
                GcEnumText.ParseSourceTrust(reader.GetString(5)),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetFieldValue<DateTimeOffset>(13));
        }
    }

    /// <inheritdoc />
    public async Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT head_seq FROM gc WHERE gc_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long head ? head : throw new GcNotFoundException(gcId);
    }
}
