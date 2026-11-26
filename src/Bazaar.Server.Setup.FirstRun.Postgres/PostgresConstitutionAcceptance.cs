using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>
/// Records the owner's acceptance of the current Constitution commit: the head commit of the newest <c>constitution</c> document in
/// the Vault index. With no such document there is nothing to accept, so the answer is false and the agent stays unactivated.
/// Recording twice is a no-op.
/// </summary>
public sealed class PostgresConstitutionAcceptance(NpgsqlDataSource dataSource) : IConstitutionAcceptance
{
    private const string _currentCommitSql =
        "SELECT head_commit FROM vault_docs WHERE type = 'constitution' AND head_commit IS NOT NULL ORDER BY updated_at DESC, doc_id DESC LIMIT 1";

    private const string _insertSql =
        "INSERT INTO constitution_acceptance (commit_sha, accepted_by) VALUES (@commit, @owner) ON CONFLICT DO NOTHING";

    /// <inheritdoc />
    public async Task<bool> RecordOwnerAcceptanceAsync(Guid ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var read = new NpgsqlCommand(_currentCommitSql, connection);
        if (await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string commit) return false;

        await using var insert = new NpgsqlCommand(_insertSql, connection);
        insert.Parameters.AddWithValue("commit", commit.Trim());
        insert.Parameters.AddWithValue("owner", ownerPrincipalId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
