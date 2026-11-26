using System.Globalization;
using System.Text;
using Npgsql;

namespace Bazaar.Server.Embeddings.Indexing;

/// <summary>
/// SQL for the embedding indexer. Vectors travel as pgvector text literals, so the Npgsql type plugin is not needed; the database
/// trigger on <c>vault_block_embedding</c> checks each vector against the profile's dimension.
/// </summary>
public sealed class PostgresEmbeddingIndexStore(NpgsqlDataSource dataSource) : IEmbeddingIndexStore
{
    public async Task<IReadOnlyList<PendingBlock>> ListPendingAsync(
        Guid profileId, BlockCursor? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // IS DISTINCT FROM covers both a missing row (null hash) and a stale one (hash differs).
        await using var cmd = dataSource.CreateCommand("""
            SELECT b.doc_id, b.ordinal, b.body, b.text_hash
            FROM vault_blocks b
            LEFT JOIN vault_block_embedding e
              ON e.doc_id = b.doc_id AND e.ordinal = b.ordinal AND e.profile_id = $1
            WHERE e.text_hash IS DISTINCT FROM b.text_hash
              AND ($2::uuid IS NULL OR (b.doc_id, b.ordinal) > ($2::uuid, $3::integer))
            ORDER BY b.doc_id, b.ordinal
            LIMIT $4
            """);
        cmd.Parameters.AddWithValue(profileId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = after is null ? DBNull.Value : after.DocId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        cmd.Parameters.AddWithValue(after?.Ordinal ?? 0);
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<PendingBlock>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PendingBlock(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3)));
        }

        return rows;
    }

    public async Task<int> UpsertAsync(Guid profileId, IReadOnlyList<EmbeddedBlock> embeddings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        if (embeddings.Count == 0)
        {
            return 0;
        }

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn);
        foreach (var item in embeddings)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO vault_block_embedding (doc_id, ordinal, profile_id, text_hash, embedding)
                VALUES ($1, $2, $3, $4, $5::text::halfvec)
                ON CONFLICT (doc_id, ordinal, profile_id) DO UPDATE SET text_hash = EXCLUDED.text_hash, embedding = EXCLUDED.embedding
                """);
            command.Parameters.AddWithValue(item.DocId);
            command.Parameters.AddWithValue(item.Ordinal);
            command.Parameters.AddWithValue(profileId);
            command.Parameters.AddWithValue(item.TextHash);
            command.Parameters.AddWithValue(ToVectorLiteral(item.Vector));
            batch.BatchCommands.Add(command);
        }

        return await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Formats a vector as pgvector text, for example <c>[0.5,-1,2]</c>. Values must be finite.</summary>
    public static string ToVectorLiteral(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count == 0)
        {
            throw new ArgumentException("An embedding needs at least one dimension.", nameof(vector));
        }

        var builder = new StringBuilder("[");
        for (var i = 0; i < vector.Count; i++)
        {
            if (!float.IsFinite(vector[i]))
            {
                throw new ArgumentException("Embedding values must be finite.", nameof(vector));
            }

            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }
}
