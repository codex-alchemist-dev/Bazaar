using Npgsql;

namespace Bazaar.Server.Vault.Data;

/// <summary>
/// The commit the Vault index has caught up to (<c>vault_state</c>). Advances are compare-and-set, so two indexers
/// cannot both move the same Vault from one commit.
/// </summary>
public sealed class VaultStateRepository(NpgsqlDataSource dataSource)
{
    public async Task<string?> GetIndexedCommitAsync(Guid vaultId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT indexed_commit FROM vault_state WHERE vault_id = $1");
        cmd.Parameters.AddWithValue(vaultId);
        return (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the index from <paramref name="expected"/> to <paramref name="next"/>. A null <paramref name="expected"/>
    /// means no state row exists yet. Returns false when the stored commit is not <paramref name="expected"/>.
    /// </summary>
    public async Task<bool> TryAdvanceAsync(Guid vaultId, string? expected, string next, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(next);
        if (expected is null)
        {
            await using var insert = dataSource.CreateCommand("""
                INSERT INTO vault_state (vault_id, indexed_commit) VALUES ($1, $2)
                ON CONFLICT (vault_id) DO NOTHING
                """);
            insert.Parameters.AddWithValue(vaultId);
            insert.Parameters.AddWithValue(next);
            return await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        await using var update = dataSource.CreateCommand("""
            UPDATE vault_state SET indexed_commit = $2::char(40)
            WHERE vault_id = $1 AND indexed_commit = $3::char(40)
            """);
        update.Parameters.AddWithValue(vaultId);
        update.Parameters.AddWithValue(next);
        update.Parameters.AddWithValue(expected);
        return await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
