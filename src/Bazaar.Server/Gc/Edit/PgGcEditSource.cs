using Bazaar.Server.Gc.Model;
using Npgsql;

namespace Bazaar.Server.Gc.Edit;

/// <summary>What an edit needs to know about the message as it stands: the header, the original author and time, and the current version.</summary>
/// <param name="Gc">The GC header, for the archive check.</param>
/// <param name="AuthorId">Who wrote the original message.</param>
/// <param name="CreatedAt">When the original message was written (the edit window is measured from here).</param>
/// <param name="Version">Highest stored version number.</param>
/// <param name="VersionSeq">Seq of the event that created that version.</param>
/// <param name="BodyCiphertext">The sealed body of that version.</param>
/// <param name="KeyId">The key that version is sealed under.</param>
/// <param name="Nonce">The cipher nonce of that version, or empty.</param>
public sealed record GcEditSnapshot(
    GcRecord Gc,
    Guid AuthorId,
    DateTimeOffset CreatedAt,
    int Version,
    long VersionSeq,
    ReadOnlyMemory<byte> BodyCiphertext,
    Guid KeyId,
    ReadOnlyMemory<byte> Nonce);

/// <summary>Reads the state an edit is planned against (docs/design/01, Edits). Read only; the writer does the append.</summary>
public sealed class PgGcEditSource(NpgsqlDataSource dataSource)
{
    private const string _gcSql = "SELECT parent_gc_id, type_key, archived_at FROM gc WHERE gc_id = $1";

    private const string _messageSql = """
        SELECT m.author_id, e.created_at, v.version, v.seq, v.body_ct, v.body_nonce, v.key_id
        FROM gc_message m
        JOIN gc_event e ON e.gc_id = m.gc_id AND e.seq = m.first_seq
        CROSS JOIN LATERAL (
            SELECT version, seq, body_ct, body_nonce, key_id
            FROM gc_message_version
            WHERE gc_id = m.gc_id AND message_id = m.message_id
            ORDER BY version DESC
            LIMIT 1) v
        WHERE m.gc_id = $1 AND m.message_id = $2
        """;

    /// <summary>Loads the snapshot, or null when the GC or the message does not exist.</summary>
    public async Task<GcEditSnapshot?> LoadAsync(Guid gcId, Guid messageId, CancellationToken ct)
    {
        GcRecord? gc = null;
        await using (var gcCmd = dataSource.CreateCommand(_gcSql))
        {
            gcCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
            await using var reader = await gcCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                gc = new GcRecord(
                    gcId,
                    reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        if (gc is null)
            return null;

        await using var cmd = dataSource.CreateCommand(_messageSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await using var row = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await row.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var ciphertext = row.GetFieldValue<byte[]>(4);
        var nonce = row.IsDBNull(5) ? Array.Empty<byte>() : row.GetFieldValue<byte[]>(5);
        return new GcEditSnapshot(
            gc,
            row.GetGuid(0),
            row.GetFieldValue<DateTimeOffset>(1),
            Convert.ToInt32(row.GetValue(2), System.Globalization.CultureInfo.InvariantCulture),
            row.GetInt64(3),
            ciphertext,
            row.GetGuid(6),
            nonce);
    }
}
