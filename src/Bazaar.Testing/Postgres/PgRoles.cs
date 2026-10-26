using Npgsql;

namespace Bazaar.Testing.Postgres;

/// <summary>
/// Makes sure a cluster-wide role exists before a test database uses it. Roles belong to the whole cluster, not to one
/// database, so every database a test run creates shares them. A check-then-create can therefore race, and the loser
/// fails with <c>duplicate_object</c> (42710) or <c>unique_violation</c> (23505), depending on timing. Both mean "already
/// there", so both are tolerated. Creating a role also needs CREATEROLE, so an existing role never needs that privilege.
/// </summary>
public static class PgRoles
{
    /// <summary>Creates a NOLOGIN role if it is missing; a no-op when it already exists or another session creates it first.</summary>
    public static async Task EnsureNoLoginRoleAsync(NpgsqlDataSource dataSource, string role, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        // The name is spliced into the DO block, so accept only a plain identifier.
        if (string.IsNullOrEmpty(role) || !role.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Role names here are plain identifiers.", nameof(role));

        var sql = $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN
                    CREATE ROLE {role} NOLOGIN;
                END IF;
            EXCEPTION WHEN duplicate_object OR unique_violation THEN
                NULL; -- created concurrently by another database on the cluster
            END
            $$
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
