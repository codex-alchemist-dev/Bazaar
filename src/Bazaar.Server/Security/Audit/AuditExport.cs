using System.Text.Json;

namespace Bazaar.Server.Security.Audit;

/// <summary>
/// Export format for the audit log: one JSON object per line, in seq order. Payload stays a string so the
/// bytes the hash covers survive a round trip unchanged. The signed daily export (doc 15) wraps these lines.
/// </summary>
public static class AuditExport
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string ToJsonLines(IEnumerable<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        using var writer = new StringWriter();
        foreach (var record in records)
        {
            writer.Write(JsonSerializer.Serialize(ToDto(record), Options));
            writer.Write('\n');
        }

        return writer.ToString();
    }

    public static IReadOnlyList<AuditRecord> FromJsonLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var records = new List<AuditRecord>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dto = JsonSerializer.Deserialize<AuditRecordDto>(line, Options)
                ?? throw new FormatException("Audit export line is empty.");
            records.Add(FromDto(dto));
        }

        return records;
    }

    private static AuditRecordDto ToDto(AuditRecord record) => new(
        record.Seq,
        record.Ts,
        record.Actor,
        record.Kind,
        record.Payload,
        Convert.ToBase64String(record.PrevHash),
        Convert.ToBase64String(record.Hash));

    private static AuditRecord FromDto(AuditRecordDto dto) => new(
        dto.Seq,
        dto.Ts,
        dto.Actor,
        dto.Kind,
        dto.Payload,
        Convert.FromBase64String(dto.PrevHash),
        Convert.FromBase64String(dto.Hash));

    private sealed record AuditRecordDto(
        long Seq,
        DateTimeOffset Ts,
        string Actor,
        string Kind,
        string Payload,
        string PrevHash,
        string Hash);
}
