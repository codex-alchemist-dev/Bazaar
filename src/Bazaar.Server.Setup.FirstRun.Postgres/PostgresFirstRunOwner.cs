using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>The owner is the one active <c>human</c> row with role <c>owner</c> (doc 32, one owner per instance).</summary>
public sealed class PostgresFirstRunOwner(NpgsqlDataSource dataSource) : IFirstRunOwner
{
    /// <inheritdoc />
    public async Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id FROM human WHERE role = 'owner' AND status = 'active'");
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id ? id : null;
    }
}
