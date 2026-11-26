using Npgsql;

namespace Bazaar.Server.GcMetadata;

/// <summary>Postgres <see cref="IDmDirectory"/>: a GC of type <c>dm</c> and its members without a <c>left_seq</c>.</summary>
public sealed class PgDmDirectory(NpgsqlDataSource dataSource) : IDmDirectory
{
    /// <inheritdoc />
    public async Task<DmInfo?> FindAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT m.principal_id
            FROM gc g
            LEFT JOIN gc_member_current m ON m.gc_id = g.gc_id AND m.left_seq IS NULL
            WHERE g.gc_id = $1 AND g.type_key = 'dm'
            """);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var found = false;
        var members = new List<Guid>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found = true;
            if (!reader.IsDBNull(0))
                members.Add(reader.GetGuid(0));
        }

        return found ? new DmInfo(gcId, members) : null;
    }
}
