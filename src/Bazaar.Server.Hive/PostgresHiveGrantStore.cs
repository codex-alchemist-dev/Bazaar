using Bazaar.Server.Hive.Abstractions;
using Npgsql;

namespace Bazaar.Server.Hive;

/// <summary>Raw Npgsql over <c>hive_grants</c> (migration components/hive/0001), on the shared data source.</summary>
public sealed class PostgresHiveGrantStore(NpgsqlDataSource dataSource) : IHiveGrantStore
{
    public async Task<bool> AddAsync(HiveGrant grant, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO hive_grants (from_project, to_project, kind, granted_by, granted_at) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.FromProject.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.ToProject.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.Kind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.GrantedBy });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = grant.GrantedAt });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RemoveAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM hive_grants WHERE from_project = $1 AND to_project = $2 AND kind = $3");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = source.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> ExistsAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM hive_grants WHERE from_project = $1 AND to_project = $2 AND kind = $3)");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = source.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT from_project, to_project, kind, granted_by, granted_at FROM hive_grants ORDER BY from_project, to_project, kind");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<HiveGrant>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HiveGrant(
                new ProjectId(reader.GetString(0)), new ProjectId(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }
}
