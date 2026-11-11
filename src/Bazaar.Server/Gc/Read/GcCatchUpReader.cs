using Bazaar.Contracts.Gc;
using Npgsql;

namespace Bazaar.Server.Gc.Read;

/// <summary>What one GC holds for a viewer who has not read it yet (docs/design/01, "What happened while I was away").</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="HeadSeq">The GC's last assigned seq.</param>
/// <param name="LastReadSeq">The viewer's read position in the GC (0 when the viewer has never read it).</param>
/// <param name="UnreadEvents">Events after <paramref name="LastReadSeq"/> that the viewer did not write.</param>
/// <param name="UnreadMentions">Mentions of the viewer after <paramref name="LastReadSeq"/>.</param>
public sealed record GcCatchUpItem(Guid GcId, long HeadSeq, long LastReadSeq, long UnreadEvents, long UnreadMentions);

/// <summary>
/// Builds the "since you were away" view: one row per GC that has events the viewer has not read (docs/design/01, Software reactions
/// and catch-up). The owner sees every live GC; any other principal sees the GCs where they are an active member. Archived GCs and
/// GCs the viewer left are excluded, because a former member keeps only what they saw.
/// </summary>
/// <remarks>
/// The read is one statement over <c>gc</c>, <c>gc_member_current</c>, <c>read_state</c>, <c>gc_event</c> and <c>gc_mention</c>, so it
/// does not load bodies. It does not replace <see cref="IGcReadAccess"/>: the membership test here is the same rule for live members,
/// and DM and Thinking GC access requests are not counted until they are granted and become membership rows.
/// </remarks>
public sealed class GcCatchUpReader(NpgsqlDataSource dataSource)
{
    // $1 = viewer, $2 = true when the viewer is the owner (every live GC is visible).
    private const string _sql = """
        WITH visible AS (
            SELECT g.gc_id, g.head_seq
            FROM gc g
            WHERE g.archived_at IS NULL
              AND g.head_seq > 0
              AND ($2 OR EXISTS (
                    SELECT 1 FROM gc_member_current m
                    WHERE m.gc_id = g.gc_id AND m.principal_id = $1 AND m.left_seq IS NULL))
        ),
        positioned AS (
            SELECT v.gc_id, v.head_seq, COALESCE(r.last_read_seq, 0) AS last_read_seq
            FROM visible v
            LEFT JOIN read_state r ON r.principal_id = $1 AND r.gc_id = v.gc_id
        )
        SELECT p.gc_id, p.head_seq, p.last_read_seq,
               (SELECT count(*) FROM gc_event e
                 WHERE e.gc_id = p.gc_id AND e.seq > p.last_read_seq AND e.author_id <> $1) AS unread_events,
               (SELECT count(*) FROM gc_mention m
                 WHERE m.gc_id = p.gc_id AND m.seq > p.last_read_seq AND m.target_principal_id = $1) AS unread_mentions
        FROM positioned p
        WHERE p.head_seq > p.last_read_seq
        ORDER BY p.gc_id
        """;

    /// <summary>The GCs with unread events for the viewer, ordered by GC id. Empty when nothing is unread.</summary>
    public async Task<IReadOnlyList<GcCatchUpItem>> GetAsync(GcPrincipal viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = viewer.Id });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = viewer.Kind == PrincipalKind.Owner });

        var items = new List<GcCatchUpItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new GcCatchUpItem(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return items;
    }
}
