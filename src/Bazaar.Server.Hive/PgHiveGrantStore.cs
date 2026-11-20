using Bazaar.Server.Hive.Abstractions;
using Npgsql;

namespace Bazaar.Server.Hive;

/// <summary>
/// Postgres grant matrix (<c>hive_grant</c>, migration components/hive/0001, doc 25). Same behavior as
/// <see cref="InMemoryHiveGrantStore"/>: adding an existing grant changes nothing, and listing is in grant order.
/// </summary>
public sealed class PgHiveGrantStore(NpgsqlDataSource dataSource) : IHiveGrantStore
{
    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM hive_grant WHERE from_project = @from AND to_project = @to AND kind = @kind)");
        cmd.Parameters.AddWithValue("from", fromProject);
        cmd.Parameters.AddWithValue("to", toProject);
        cmd.Parameters.AddWithValue("kind", HiveKinds.Name(kind));
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT from_project, to_project, kind, granted_by FROM hive_grant ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var grants = new List<HiveGrant>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            grants.Add(new HiveGrant(reader.GetString(0), reader.GetString(1), ParseKind(reader.GetString(2)), reader.GetString(3)));
        }

        return grants;
    }

    /// <inheritdoc />
    public async Task AddAsync(HiveGrant grant, CancellationToken ct)
    {
        HiveGrantRules.Validate(grant);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO hive_grant (from_project, to_project, kind, granted_by)
            VALUES (@from, @to, @kind, @granted_by)
            ON CONFLICT (from_project, to_project, kind) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("from", grant.FromProject);
        cmd.Parameters.AddWithValue("to", grant.ToProject);
        cmd.Parameters.AddWithValue("kind", HiveKinds.Name(grant.Kind));
        cmd.Parameters.AddWithValue("granted_by", grant.GrantedBy);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM hive_grant WHERE from_project = @from AND to_project = @to AND kind = @kind");
        cmd.Parameters.AddWithValue("from", fromProject);
        cmd.Parameters.AddWithValue("to", toProject);
        cmd.Parameters.AddWithValue("kind", HiveKinds.Name(kind));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    internal static HiveKind ParseKind(string name) =>
        HiveKinds.TryParse(name, out var kind)
            ? kind
            : throw new InvalidOperationException($"hive tables hold an unknown kind '{name}'.");
}
