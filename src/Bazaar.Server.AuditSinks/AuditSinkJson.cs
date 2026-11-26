using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.AuditSinks;

/// <summary>One JSON encoding of rows and checkpoints, shared by the file and replica sinks so a verifier reads both the same way.</summary>
internal static class AuditSinkJson
{
    public static string Row(AuditSinkRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "row");
            w.WriteNumber("seq", row.Seq);
            w.WriteString("ts", row.Ts);
            w.WriteString("actor", row.Actor);
            w.WriteString("kind", row.Kind);
            w.WriteString("payload", row.Payload);
            w.WriteString("prevHash", Convert.ToBase64String(row.PrevHash));
            w.WriteString("hash", Convert.ToBase64String(row.Hash));
            w.WriteString("key", row.IdempotencyKey);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Checkpoint(AuditSinkCheckpoint cp)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "checkpoint");
            w.WriteNumber("firstSeq", cp.FirstSeq);
            w.WriteNumber("lastSeq", cp.LastSeq);
            w.WriteNumber("rowCount", cp.RowCount);
            w.WriteString("chainHeadHash", Convert.ToBase64String(cp.ChainHeadHash));
            w.WriteString("merkleRoot", Convert.ToBase64String(cp.MerkleRoot));
            w.WriteString("createdAt", cp.CreatedAt);
            w.WriteString("keyId", cp.KeyId);
            w.WriteString("signature", Convert.ToBase64String(cp.Signature));
            w.WriteString("key", cp.IdempotencyKey);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A flat JSON object of string members, in the order given.</summary>
    public static string Object(IEnumerable<KeyValuePair<string, string>> members)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (name, value) in members)
            {
                w.WriteString(name, value);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads the type and ordering number of a line written by this class: <c>seq</c> for rows, <c>lastSeq</c> for checkpoints.</summary>
    public static (string Type, long Order) Classify(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? throw new FormatException("Audit sink line has no type.");
        var order = type == "row" ? root.GetProperty("seq").GetInt64() : root.GetProperty("lastSeq").GetInt64();
        return (type, order);
    }
}
