using System.Text.Json.Nodes;
using Bazaar.Archive.Gc;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Gc.Archive;

/// <summary>
/// What the cold-storage module needs from the GC log (doc 25 "Archive cold storage", doc 40 section 5a). It reads message text
/// through the body codec (so an erased author reads as null), reports Thinking GCs so their text is never archived, and sets
/// <c>archived_at</c> after writing an <c>archived</c> system event. Nothing is deleted.
/// </summary>
public sealed class PgGcArchiveBridge(NpgsqlDataSource dataSource, IGcLogWriter writer, IGcBodyCodec codec, TimeProvider? time = null) : IGcArchiveBridge, IGcMessageTextSource
{
    /// <summary>The system author of the archive event: a fixed id so the event is attributable and the append is retry-safe.</summary>
    public static readonly Guid ArchiverId = Guid.Parse("00000000-0000-7000-8000-0000000a2c41");

    /// <summary>The GC type whose text is never archived.</summary>
    public const string ThinkingTypeKey = "thinking";

    private const string _typeSql = "SELECT type_key FROM gc WHERE gc_id = $1";

    private const string _bodySql = """
        SELECT body_ct, body_nonce, key_id
        FROM gc_message_version
        WHERE gc_id = $1 AND message_id = $2 AND version = $3 AND NOT tombstone
        """;

    private const string _markSql = "UPDATE gc SET archived_at = $2 WHERE gc_id = $1 AND archived_at IS NULL";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<bool> IsThinkingGcAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_typeSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        var type = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return type is string key
            ? key == ThinkingTypeKey
            : throw new GcNotFoundException(gcId);
    }

    /// <inheritdoc />
    public async Task<string?> GetMessageTextAsync(Guid gcId, Guid messageId, int version, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_bodySql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = version });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(2))
            return null;

        var ciphertext = (byte[])reader.GetValue(0);
        var nonce = reader.IsDBNull(1) ? [] : (byte[])reader.GetValue(1);
        return await codec.OpenAsync(ciphertext, reader.GetGuid(2), nonce, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two steps, each safe to repeat: the system event first (the writer refuses new events once the GC is archived), then the
    /// <c>archived_at</c> stamp. A GC the owner already archived is left as it is.
    /// </remarks>
    public async Task MarkGcArchivedAsync(Guid gcId, string rootHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootHash);

        var payload = new JsonObject { ["action"] = "archived", ["actor"] = ArchiverId.ToString("D"), ["root_hash"] = rootHash };
        var request = new AppendRequest(gcId, GcEventKinds.System, AuthorKind.System, ArchiverId, SourceTrust.System, "archive:" + rootHash)
        {
            PayloadJson = payload.ToJsonString(),
        };

        try
        {
            await writer.AppendAsync(request, ct).ConfigureAwait(false);
        }
        catch (GcArchivedException)
        {
            return;
        }

        await using var cmd = dataSource.CreateCommand(_markSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = _time.GetUtcNow() });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
