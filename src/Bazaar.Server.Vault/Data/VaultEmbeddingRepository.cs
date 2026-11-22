using System.Globalization;
using System.Text;
using Npgsql;

namespace Bazaar.Server.Vault.Data;

/// <summary>
/// Block embeddings for one embedding profile (<c>vault_block_embedding</c>, doc 18). Vectors travel as pgvector text
/// literals, so the Npgsql type plugin is not needed. The dimension check lives in the database trigger.
/// </summary>
public sealed class VaultEmbeddingRepository(NpgsqlDataSource dataSource)
{
    /// <summary>The text hash each block of one document has under one profile. Blocks missing here need embedding.</summary>
    public async Task<IReadOnlyDictionary<int, byte[]>> GetStoredHashesAsync(
        Guid docId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT ordinal, text_hash FROM vault_block_embedding WHERE doc_id = $1 AND profile_id = $2
            """);
        cmd.Parameters.AddWithValue(docId);
        cmd.Parameters.AddWithValue(profileId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var hashes = new Dictionary<int, byte[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes[reader.GetInt32(0)] = reader.GetFieldValue<byte[]>(1);
        }

        return hashes;
    }

    /// <summary>Inserts or replaces embeddings for the given blocks of one document. Returns the number of rows written.</summary>
    public async Task<int> UpsertAsync(
        Guid docId, Guid profileId, IReadOnlyList<VaultBlockEmbedding> embeddings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        if (embeddings.Count == 0)
        {
            return 0;
        }

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn);
        foreach (var embedding in embeddings)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO vault_block_embedding (doc_id, ordinal, profile_id, text_hash, embedding)
                VALUES ($1, $2, $3, $4, $5::text::halfvec)
                ON CONFLICT (doc_id, ordinal, profile_id) DO UPDATE SET text_hash = EXCLUDED.text_hash, embedding = EXCLUDED.embedding
                """);
            command.Parameters.AddWithValue(docId);
            command.Parameters.AddWithValue(embedding.Ordinal);
            command.Parameters.AddWithValue(profileId);
            command.Parameters.AddWithValue(embedding.TextHash);
            command.Parameters.AddWithValue(ToVectorLiteral(embedding.Vector));
            batch.BatchCommands.Add(command);
        }

        return await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops every embedding under one profile, for a profile change. Other profiles are untouched.</summary>
    public async Task<int> DeleteForProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM vault_block_embedding WHERE profile_id = $1");
        cmd.Parameters.AddWithValue(profileId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Formats a vector as pgvector text, for example <c>[0.5,-1,2]</c>. Values must be finite.</summary>
    internal static string ToVectorLiteral(IReadOnlyList<float> vector)
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
