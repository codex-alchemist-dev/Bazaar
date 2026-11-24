using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;
using Npgsql;

namespace Bazaar.Server.Gc.Members;

/// <summary>Reads the state a membership change is planned against. Read only; the writer does the append.</summary>
public sealed class PgGcMembershipSource(NpgsqlDataSource dataSource) : IGcMembershipSource
{
    private const string _gcSql = "SELECT parent_gc_id, type_key, archived_at, head_seq FROM gc WHERE gc_id = $1";

    private const string _membersSql = "SELECT principal_id, role, joined_seq, left_seq FROM gc_member_current WHERE gc_id = $1";

    /// <summary>Loads the snapshot, or null when the GC does not exist.</summary>
    public async Task<GcMembershipSnapshot?> LoadAsync(Guid gcId, CancellationToken ct)
    {
        GcRecord? gc = null;
        long head = 0;
        await using (var gcCmd = dataSource.CreateCommand(_gcSql))
        {
            gcCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
            await using var reader = await gcCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                gc = new GcRecord(
                    gcId,
                    reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
                head = reader.GetInt64(3);
            }
        }

        if (gc is null)
            return null;

        var members = new List<GcMemberRecord>();
        await using var cmd = dataSource.CreateCommand(_membersSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var rows = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rows.ReadAsync(ct).ConfigureAwait(false))
        {
            // The kind is not stored on gc_member; the planners only need it for the acting viewer, so it is left as human here.
            members.Add(new GcMemberRecord(
                gcId,
                rows.GetGuid(0),
                PrincipalKind.Human,
                Bazaar.Server.Gc.GcEnumText.ParseMemberRole(rows.GetString(1)),
                rows.GetInt64(2),
                rows.IsDBNull(3) ? null : rows.GetInt64(3)));
        }

        return new GcMembershipSnapshot(gc, head, members);
    }
}

/// <summary>Lists a principal's GC memberships from <c>gc_member_current</c> (docs/design/02, GC list).</summary>
public sealed class PgGcMemberListSource(NpgsqlDataSource dataSource) : IGcMemberListSource
{
    // $1 = principal, $2 = include archived. The last-activity time is the created_at of the entry at head_seq.
    private const string _sql = """
        SELECT g.gc_id, g.title, g.type_key, g.head_seq, (g.archived_at IS NOT NULL) AS archived,
               (SELECT count(*) FROM gc_member_current c WHERE c.gc_id = g.gc_id AND c.left_seq IS NULL)::int AS member_count,
               (SELECT e.created_at FROM gc_event e WHERE e.gc_id = g.gc_id AND e.seq = g.head_seq) AS last_activity
        FROM gc g
        JOIN gc_member_current m ON m.gc_id = g.gc_id AND m.principal_id = $1 AND m.left_seq IS NULL
        WHERE ($2 OR g.archived_at IS NULL)
        ORDER BY last_activity DESC NULLS LAST, g.gc_id
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GcMemberListing>> ListAsync(Guid principalId, bool includeArchived, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = includeArchived });

        var items = new List<GcMemberListing>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new GcMemberListing(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return items;
    }
}
