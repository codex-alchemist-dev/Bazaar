using Npgsql;

namespace Bazaar.Server.GitHub.Pr;

/// <summary>
/// The company owner's principal id, resolved without a setting (doc 21, Owner membership of PR and issue GCs). A module-local port, so
/// the GitHub module binds to no identity or setup module.
/// </summary>
public interface IGitHubOwnerResolver
{
    /// <summary>The owner's principal id, or null when no owner exists yet (a fresh install before first run).</summary>
    Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the owner from the <c>human</c> table: the one active row with role <c>owner</c> (the remote migration keeps at most one;
/// the first-run seed reads the same row). A found owner is cached, since the id never changes; a missing owner or a missing table
/// answers null and is asked again at the next event.
/// </summary>
public sealed class PgGitHubOwnerResolver(NpgsqlDataSource dataSource) : IGitHubOwnerResolver
{
    private const string _sql = "SELECT id FROM human WHERE role = 'owner' AND status = 'active' LIMIT 1";
    private Guid? _cached;

    /// <inheritdoc />
    public async Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } known)
            return known;
        try
        {
            await using var cmd = dataSource.CreateCommand(_sql);
            var owner = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
            _cached = owner;
            return owner;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The remote schema is not migrated yet: no owner exists.
            return null;
        }
    }
}
