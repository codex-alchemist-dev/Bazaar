using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>
/// The current Constitution commit from the Vault index (doc 18 index, doc 24): the <c>head_commit</c> of the most recently
/// updated Vault document of type <c>constitution</c>. Null when the Vault has no such document yet, or it has no commit. This reads
/// the rebuildable index only; the owner's acceptance of the returned commit is recorded by <see cref="PostgresConstitutionAcceptance"/>.
/// </summary>
public sealed class VaultCurrentConstitutionCommit(NpgsqlDataSource dataSource) : ICurrentConstitutionCommit
{
    /// <inheritdoc />
    public async Task<string?> GetCurrentCommitShaAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT head_commit FROM vault_docs WHERE type = 'constitution' AND head_commit IS NOT NULL ORDER BY updated_at DESC, doc_id DESC LIMIT 1");
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string sha ? sha : null;
    }
}
