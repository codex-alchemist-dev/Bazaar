using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;
using Npgsql;

namespace Bazaar.Server.Gc.Members;

/// <summary>What a membership plan needs: the header, the head seq the plan is made against, and every current membership row.</summary>
/// <param name="Gc">The GC header.</param>
/// <param name="HeadSeq">The head seq read with the header. The writer rejects the write when the head has moved.</param>
/// <param name="Members">The current membership rows, one per principal.</param>
public sealed record GcMembershipSnapshot(GcRecord Gc, long HeadSeq, IReadOnlyCollection<GcMemberRecord> Members);

/// <summary>Reads the state a membership change is planned against. Read only; the writer does the append.</summary>
public sealed class PgGcMembershipSource(NpgsqlDataSource dataSource)
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
