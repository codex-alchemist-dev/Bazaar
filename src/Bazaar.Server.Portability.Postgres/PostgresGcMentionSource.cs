using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>
/// <see cref="IGcMentionSource"/> over <c>gc_mention</c> (docs/design/01, doc 04, doc 44 section 4.2). The page key is the table's
/// primary key <c>(gc_id, seq, target_principal_id)</c>, so a page is one index range scan inside one partition. A seq can carry
/// several mentions, so the key is the (seq, target) pair, not seq alone. Target order is PostgreSQL's <c>uuid</c> order (bytewise),
/// and the same order is used for the key comparison and the sort, so pages never skip or repeat a row.
/// </summary>
public sealed class PostgresGcMentionSource(NpgsqlDataSource dataSource, IGcReadAccess access) : IGcMentionSource
{
    private const string _readSql = """
        SELECT seq, target_principal_id
        FROM gc_mention
        WHERE gc_id = $1
          AND (seq > $2 OR (seq = $2 AND target_principal_id > $3))
          AND seq <= $4
        ORDER BY seq, target_principal_id
        LIMIT $5
        """;

    /// <inheritdoc />
    public async IAsyncEnumerable<GcMentionRecord> ReadAsync(
        Guid gcId, long afterSeq, Guid afterTarget, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
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
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = afterTarget });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = grant.ThroughSeq ?? long.MaxValue });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new GcMentionRecord(reader.GetInt64(0), reader.GetGuid(1));
        }
    }
}
