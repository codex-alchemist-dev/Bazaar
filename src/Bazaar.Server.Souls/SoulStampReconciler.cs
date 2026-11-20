using Bazaar.Souls.Abstractions;
using Npgsql;

namespace Bazaar.Server.Souls;

/// <summary>
/// Finds Soul stamps in the GC log that no longer name a real <c>soul_versions</c> row. There is no foreign key between GC and
/// Souls (owner, 2026-11-16), so this check is what notices a dangling stamp after a restore or an erase. It is read-only and
/// bounded; it never repairs, because history is append-only and a repair would be a rewrite.
/// </summary>
/// <remarks>
/// It reads the two stamp columns of <c>gc_event</c> and <c>gc_message_version</c> (migration <c>gc/0001</c>). That is the one
/// place Souls looks at GC tables; it moves to the GC module with the stamp columns when GC is extracted.
/// </remarks>
public sealed class SoulStampReconciler(NpgsqlDataSource dataSource)
{
    /// <summary>The most dangling stamps one run reports.</summary>
    public const int DefaultLimit = 100;

    private const string _danglingSql = """
        SELECT soul_id, soul_version FROM (
            SELECT DISTINCT e.soul_id, e.soul_version FROM gc_event e
            WHERE e.soul_id IS NOT NULL
            UNION
            SELECT DISTINCT m.soul_id, m.soul_version FROM gc_message_version m
            WHERE m.soul_id IS NOT NULL
        ) s
        WHERE NOT EXISTS (SELECT 1 FROM soul_versions v WHERE v.soul_id = s.soul_id AND v.version = s.soul_version)
        ORDER BY soul_id, soul_version
        LIMIT $1
        """;

    /// <summary>The dangling stamps, sorted, up to <paramref name="limit"/>. Empty means every stamp resolves.</summary>
    public async Task<IReadOnlyList<SoulRef>> FindDanglingAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand(_danglingSql);
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<SoulRef>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            found.Add(new SoulRef(reader.GetGuid(0), reader.GetInt32(1)));
        return found;
    }
}
