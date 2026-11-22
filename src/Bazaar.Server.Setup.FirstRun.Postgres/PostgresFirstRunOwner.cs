using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>The owner is the one active <c>human</c> row with role <c>owner</c> (the remote migration keeps at most one).</summary>
public sealed class PostgresFirstRunOwner(NpgsqlDataSource dataSource) : IFirstRunOwner
{
    private const string _sql = "SELECT id FROM human WHERE role = 'owner' AND status = 'active'";

    /// <inheritdoc />
    public async Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(_sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }
}
