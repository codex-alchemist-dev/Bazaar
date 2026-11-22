using Bazaar.Server.Setup.Http;
using Npgsql;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// Answers whether the install has an owner: a <c>human</c> row with role <c>owner</c> (the remote migration, which keeps at most
/// one by a unique index). A database error is not caught, so the gate fails the call with a server error instead of guessing.
/// </summary>
public sealed class PostgresSetupOwnerDirectory(NpgsqlDataSource dataSource) : ISetupOwnerDirectory
{
    private const string _sql = "SELECT EXISTS (SELECT 1 FROM human WHERE role = 'owner')";

    /// <inheritdoc />
    public async Task<bool> OwnerExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
