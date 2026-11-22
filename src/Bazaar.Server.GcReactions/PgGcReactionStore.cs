using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.GcReactions.Abstractions;
using Npgsql;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// Reads reactions from <c>gc_event</c> (kind <c>reaction</c>, registered by migration components/gc_reactions/0001). Every query
/// pins <c>gc_id</c>, so Postgres prunes to one hash partition. The rows are the log; this never writes.
/// </summary>
public sealed class PgGcReactionStore(NpgsqlDataSource dataSource) : IGcReactionStore
{
    /// <inheritdoc />
    public async Task<bool> MessageExistsAsync(Guid gcId, long n, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM gc_event WHERE gc_id = $1 AND seq = $2 AND kind = $3)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = n });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = GcEventKinds.Message });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReactionEventRow>> ReadAsync(Guid gcId, long? targetN, long? afterN, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT seq, author_id, target_seq, payload::text
            FROM gc_event
            WHERE gc_id = $1 AND kind = $2
              AND ($3::bigint IS NULL OR target_seq = $3)
              AND ($4::bigint IS NULL OR target_seq > $4)
            ORDER BY seq
            """);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = GcEventKinds.Reaction });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)targetN ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)afterN ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });

        var rows = new List<ReactionEventRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2) || reader.IsDBNull(3))
                continue;
            if (ReactionPayload.TryParse(reader.GetString(3), out var emoji, out var add))
                rows.Add(new ReactionEventRow(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), emoji, add));
        }

        return rows;
    }
}

/// <summary>The payload of a <c>reaction</c> event: <c>{"e":"&lt;emoji&gt;","op":"add"|"remove"}</c>.</summary>
public static class ReactionPayload
{
    /// <summary>The JSON text to store for a reaction.</summary>
    public static string Write(string emoji, bool add) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["e"] = emoji, ["op"] = add ? "add" : "remove" }, ReactionPayloadJson.Default.DictionaryStringString);

    /// <summary>Reads a stored payload. False for a shape this build does not know, so a newer writer's rows are skipped, not fatal.</summary>
    public static bool TryParse(string json, out string emoji, out bool add)
    {
        emoji = string.Empty;
        add = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("e", out var e) || e.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
                return false;
            var opText = op.GetString();
            if (opText is not ("add" or "remove") || e.GetString() is not { Length: > 0 } text)
                return false;
            emoji = text;
            add = opText == "add";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ReactionPayloadJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
