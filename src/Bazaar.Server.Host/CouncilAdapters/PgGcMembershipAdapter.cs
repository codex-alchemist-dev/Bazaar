using Bazaar.Server.Council.Service;
using Npgsql;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// Production <see cref="IGcMembership"/>: the GC's current members from <c>gc_member_current</c>, minus members who left.
/// <c>gc_member</c> stores no principal kind, so the kind comes from <c>principal_handle.principal_kind</c> and, for an agent
/// without a handle row, from the <c>agent</c> table. Owners and agents are returned; humans and principals of unknown kind are
/// skipped, because the Council electorate is the owner and the agents (doc 10).
/// </summary>
public sealed class PgGcMembershipAdapter(NpgsqlDataSource dataSource) : IGcMembership
{
    internal const string MembersSql = """
        SELECT m.principal_id,
               COALESCE(h.principal_kind, CASE WHEN a.agent_id IS NOT NULL THEN 'agent' END)
        FROM gc_member_current m
        LEFT JOIN principal_handle h ON h.principal_id = m.principal_id
        LEFT JOIN agent a ON a.agent_id = m.principal_id
        WHERE m.gc_id = $1 AND m.left_seq IS NULL
        ORDER BY m.principal_id
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GcMember>> ListMembersAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(MembersSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        var result = new List<GcMember>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var member = Map(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            if (member is not null)
            {
                result.Add(member);
            }
        }

        return result;
    }

    /// <summary>Maps a principal kind text to a Council member, or null for a human or an unknown kind.</summary>
    internal static GcMember? Map(Guid principalId, string? kind) => kind switch
    {
        "owner" => new GcMember(principalId.ToString("D"), GcMemberKind.Owner),
        "agent" => new GcMember(principalId.ToString("D"), GcMemberKind.Agent),
        _ => null,
    };
}
