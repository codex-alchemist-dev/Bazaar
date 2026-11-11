using Npgsql;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>
/// The Postgres <see cref="IGcMentionDirectory"/>: a GC's current members (no <c>left_seq</c>) joined to their handles in
/// <c>principal_handle</c> (<c>mentions/0001</c>). A member without a handle is not in the roster, so it cannot be mentioned;
/// a principal with a handle that is not a current member is not in it either.
/// </summary>
/// <remarks>
/// The owner id is the <c>human</c> row with role <c>owner</c> (<c>remote/0001</c>), or <see cref="Guid.Empty"/> when the
/// instance has no owner row yet. The owner is only parsed from a post when it is a current member with a handle, like anyone else.
/// </remarks>
public sealed class SqlGcMentionDirectory(NpgsqlDataSource dataSource) : IGcMentionDirectory
{
    private const string _membersSql = """
        SELECT m.principal_id, h.handle
        FROM gc_member_current m
        JOIN principal_handle h ON h.principal_id = m.principal_id
        WHERE m.gc_id = $1 AND m.left_seq IS NULL
        ORDER BY h.handle
        """;

    private const string _ownerSql = "SELECT id FROM human WHERE role = 'owner' LIMIT 1";

    /// <inheritdoc />
    public async Task<GcMentionRoster> LoadAsync(Guid gcId, CancellationToken ct)
    {
        var members = new List<GcMentionMember>();
        await using (var cmd = dataSource.CreateCommand(_membersSql))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                members.Add(new GcMentionMember(reader.GetGuid(0), reader.GetString(1)));
        }

        await using var ownerCmd = dataSource.CreateCommand(_ownerSql);
        var ownerId = await ownerCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid id ? id : Guid.Empty;
        return new GcMentionRoster(ownerId, members);
    }
}
