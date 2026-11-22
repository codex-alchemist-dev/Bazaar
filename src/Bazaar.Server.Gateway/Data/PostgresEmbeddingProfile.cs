using Npgsql;

namespace Bazaar.Server.Gateway.Data;

/// <summary>
/// Reads the dimension of the install's active embedding profile from the existing <c>embedding_profile</c> table (docs 04, 08;
/// C39). The gateway owns no profile storage: with no active row the answer is null and the gateway skips the check.
/// </summary>
public sealed class PostgresEmbeddingProfile(NpgsqlDataSource dataSource) : IEmbeddingProfile
{
    public async ValueTask<int?> GetDimensionsAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT dimensions FROM embedding_profile WHERE active");
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int dimensions ? dimensions : null;
    }
}
