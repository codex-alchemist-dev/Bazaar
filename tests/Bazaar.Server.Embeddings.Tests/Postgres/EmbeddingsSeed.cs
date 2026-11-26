using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Bazaar.Server.Embeddings.Tests.Postgres;

/// <summary>Raw inserts for <c>vault_docs</c> and <c>vault_blocks</c>, so the store tests do not depend on the Vault module.</summary>
internal static class EmbeddingsSeed
{
    public static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public static async Task<Guid> InsertDocAsync(NpgsqlDataSource source, Guid docId, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(
            "INSERT INTO vault_docs (doc_id, vault_id, path, title, type, area) VALUES ($1, $2, $3, 'T', 'research', 'shared/research')");
        cmd.Parameters.AddWithValue(docId);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue("docs/" + docId.ToString("N") + ".md");
        await cmd.ExecuteNonQueryAsync(ct);
        return docId;
    }

    public static async Task InsertBlockAsync(NpgsqlDataSource source, Guid docId, int ordinal, string body, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(
            "INSERT INTO vault_blocks (doc_id, ordinal, start_line, end_line, text_hash, body) VALUES ($1, $2, 1, 1, $3, $4)");
        cmd.Parameters.AddWithValue(docId);
        cmd.Parameters.AddWithValue(ordinal);
        cmd.Parameters.AddWithValue(Hash(body));
        cmd.Parameters.AddWithValue(body);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<(Guid DocId, int Ordinal, byte[] Hash)> InsertBlockAsync(NpgsqlDataSource source, string body, CancellationToken ct)
    {
        var docId = await InsertDocAsync(source, Guid.CreateVersion7(), ct);
        await InsertBlockAsync(source, docId, 0, body, ct);
        return (docId, 0, Hash(body));
    }

    public static async Task UpdateBlockBodyAsync(NpgsqlDataSource source, Guid docId, int ordinal, string body, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("UPDATE vault_blocks SET body = $3, text_hash = $4 WHERE doc_id = $1 AND ordinal = $2");
        cmd.Parameters.AddWithValue(docId);
        cmd.Parameters.AddWithValue(ordinal);
        cmd.Parameters.AddWithValue(body);
        cmd.Parameters.AddWithValue(Hash(body));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
