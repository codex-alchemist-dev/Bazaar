using Npgsql;

namespace Bazaar.Server.Vault.Data;

/// <summary>
/// The Git DAG index of the Vault repo (<c>vault_commit</c>, doc 18). Commits are inserted once and never changed;
/// inserting a commit that is already indexed is a no-op, so the indexer can replay a range safely.
/// </summary>
public sealed class VaultCommitRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Inserts commits that are not yet indexed. Returns how many rows were new.</summary>
    public async Task<int> InsertAsync(IEnumerable<VaultCommitRecord> commits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var batch = new NpgsqlBatch(conn) { Transaction = tx };
        foreach (var commit in commits)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO vault_commit (vault_id, sha, parents, author, committed_at, refs, touched_paths, gc_id, task_id, is_merge)
                VALUES ($1, $2, $3::char(40)[], $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (vault_id, sha) DO NOTHING
                """);
            command.Parameters.AddWithValue(commit.VaultId);
            command.Parameters.AddWithValue(commit.Sha);
            command.Parameters.AddWithValue(commit.Parents.ToArray());
            command.Parameters.AddWithValue(commit.Author);
            command.Parameters.AddWithValue(commit.CommittedAt);
            command.Parameters.AddWithValue(commit.Refs.ToArray());
            command.Parameters.AddWithValue(commit.TouchedPaths.ToArray());
            command.Parameters.AddWithValue(commit.GcId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(commit.TaskId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(commit.Parents.Count > 1);
            batch.BatchCommands.Add(command);
        }

        if (batch.BatchCommands.Count == 0)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        // ON CONFLICT DO NOTHING reports 0 for rows already indexed, so the batch total is the number of new commits.
        var inserted = await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<VaultCommitRecord?> GetAsync(Guid vaultId, string sha, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_commitSelect + " WHERE vault_id = $1 AND sha = $2");
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(sha);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCommit(reader) : null;
    }

    /// <summary>Newest commits first, by committed time.</summary>
    public async Task<IReadOnlyList<VaultCommitRecord>> ListRecentAsync(Guid vaultId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var cmd = dataSource.CreateCommand(_commitSelect + " WHERE vault_id = $1 ORDER BY committed_at DESC, sha DESC LIMIT $2");
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var commits = new List<VaultCommitRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            commits.Add(ReadCommit(reader));
        }

        return commits;
    }

    private const string _commitSelect = """
        SELECT vault_id, sha, parents::text[], author, committed_at, refs, touched_paths, gc_id, task_id
        FROM vault_commit
        """;

    private static VaultCommitRecord ReadCommit(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetFieldValue<string[]>(2),
        reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetFieldValue<string[]>(5),
        reader.GetFieldValue<string[]>(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8));
}
