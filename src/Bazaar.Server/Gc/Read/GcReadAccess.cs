using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;
using Npgsql;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// The Postgres-backed <see cref="IGcReadAccess"/>. It loads the GC header, the current membership rows and the viewer's
/// approved access request, then asks the pure <see cref="GcAccessPolicy"/> and <see cref="ThinkingGcRules"/> rules.
/// The rules themselves live in one place; this class only fetches their inputs.
/// </summary>
/// <remarks>
/// <c>gc_member</c> has no principal-kind column: principal identity is owned by doc 14 (docs/design/04). Rows therefore
/// load as <see cref="PrincipalKind.Human"/>, except the viewer's own row, which takes the viewer's kind. That keeps
/// Thinking GC ownership working for the agent reading its own GC. For other agents the owner is not found, so
/// Thinking GC reads fail closed until doc 14 supplies kinds.
/// </remarks>
public sealed class GcReadAccess(NpgsqlDataSource dataSource) : IGcReadAccess
{
    private const string _gcSql = "SELECT parent_gc_id, type_key, archived_at FROM gc WHERE gc_id = $1";

    private const string _membersSql = """
        SELECT principal_id, role, joined_seq, left_seq
        FROM gc_member_current
        WHERE gc_id = $1
        """;

    private const string _approvedSql = """
        SELECT 1
        FROM gc_access_request
        WHERE gc_id = $1 AND requester = $2 AND status = 'approved'
        LIMIT 1
        """;

    /// <inheritdoc />
    public async ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        // An unknown GC and a GC the viewer may not read look the same to the caller: both are denied.
        var gc = await LoadGcAsync(gcId, ct).ConfigureAwait(false);
        if (gc is null)
            return GcReadGrant.Denied;

        var members = await LoadMembersAsync(gcId, viewer, ct).ConfigureAwait(false);
        var approved = await LoadApprovedAsync(gcId, viewer, ct).ConfigureAwait(false);

        var readable = GcAccessPolicy.ReadableUpToSeq(gc, members, new GcViewer(viewer.Id, viewer.Kind), approved);
        return readable switch
        {
            null => GcReadGrant.Denied,
            GcAccessPolicy.AllEvents => GcReadGrant.Full,
            var seq => GcReadGrant.Through(seq.Value),
        };
    }

    /// <summary>
    /// True when the viewer may append to the GC: readable in full, and <see cref="GcAccessPolicy.CanWrite"/> allows it
    /// (archived GCs are read-only for everyone; Thinking GCs take writes only from their owning agent and the owner).
    /// </summary>
    public async ValueTask<bool> CanWriteAsync(Guid gcId, GcPrincipal writer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var gc = await LoadGcAsync(gcId, ct).ConfigureAwait(false);
        if (gc is null)
            return false;

        var members = await LoadMembersAsync(gcId, writer, ct).ConfigureAwait(false);
        var approved = await LoadApprovedAsync(gcId, writer, ct).ConfigureAwait(false);
        var viewer = new GcViewer(writer.Id, writer.Kind);
        return GcAccessPolicy.ReadableUpToSeq(gc, members, viewer, approved) is not null
            && GcAccessPolicy.CanWrite(gc, members, viewer);
    }

    /// <summary>The principals with an active membership row (no <c>left_seq</c>), for mention checks.</summary>
    public async Task<HashSet<Guid>> LoadActiveMemberIdsAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT principal_id FROM gc_member_current WHERE gc_id = $1 AND left_seq IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<Guid>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task<GcRecord?> LoadGcAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_gcSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var parent = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
        var typeKey = reader.GetString(1);
        var archivedAt = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2);
        return new GcRecord(gcId, parent, typeKey, archivedAt);
    }

    private async Task<List<GcMemberRecord>> LoadMembersAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_membersSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var members = new List<GcMemberRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var principal = reader.GetGuid(0);
            // Only the viewer's own kind is known here (see the remarks); everyone else is treated as a human.
            var kind = principal == viewer.Id ? viewer.Kind : PrincipalKind.Human;
            var role = GcEnumText.ParseMemberRole(reader.GetString(1));
            var joinedSeq = reader.GetInt64(2);
            var leftSeq = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            members.Add(new GcMemberRecord(gcId, principal, kind, role, joinedSeq, leftSeq));
        }

        return members;
    }

    private async Task<IReadOnlySet<Guid>> LoadApprovedAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct)
    {
        // Only the viewer's own approval matters to the policy, so the query asks for that one principal.
        await using var cmd = dataSource.CreateCommand(_approvedSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = viewer.Id });
        var found = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        return found ? new HashSet<Guid> { viewer.Id } : new HashSet<Guid>();
    }
}
