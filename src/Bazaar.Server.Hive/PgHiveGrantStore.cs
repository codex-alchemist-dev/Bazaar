using System.Data.Common;
using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>
/// Postgres grant matrix (<c>hive_grant</c>, migration components/hive/0001, doc 25). Same behavior as
/// <see cref="InMemoryHiveGrantStore"/>: adding an existing grant changes nothing, and listing is in grant order.
/// </summary>
public sealed class PgHiveGrantStore(DbDataSource dataSource) : IHiveGrantStore
{
    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        await using var cmd = HiveCommands.Read(
            dataSource, "SELECT EXISTS (SELECT 1 FROM hive_grant WHERE from_project = @from AND to_project = @to AND kind = @kind)", "hive_grant");
        HiveCommands.Add(cmd, "from", fromProject);
        HiveCommands.Add(cmd, "to", toProject);
        HiveCommands.Add(cmd, "kind", HiveKinds.Name(kind));
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HiveGrant>> ListAsync(CancellationToken ct)
    {
        await using var cmd = HiveCommands.Read(
            dataSource, "SELECT from_project, to_project, kind, granted_by FROM hive_grant ORDER BY seq", "hive_grant");
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
        HiveCommands.Add(cmd, "from", grant.FromProject);
        HiveCommands.Add(cmd, "to", grant.ToProject);
        HiveCommands.Add(cmd, "kind", HiveKinds.Name(grant.Kind));
        HiveCommands.Add(cmd, "granted_by", grant.GrantedBy);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM hive_grant WHERE from_project = @from AND to_project = @to AND kind = @kind");
        HiveCommands.Add(cmd, "from", fromProject);
        HiveCommands.Add(cmd, "to", toProject);
        HiveCommands.Add(cmd, "kind", HiveKinds.Name(kind));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    internal static HiveKind ParseKind(string name) =>
        HiveKinds.TryParse(name, out var kind)
            ? kind
            : throw new InvalidOperationException($"hive tables hold an unknown kind '{name}'.");
}
