using System.Text;
using System.Text.Json;

namespace Bazaar.Kodo.Anchoring;

/// <summary>
/// Canonical serialisation of <see cref="AnchorRecord"/>: compact camelCase JSON, members in declaration order,
/// base64 hashes, UTC time. The same record always produces the same bytes, so a sink can sign or hash them.
/// </summary>
public static class AnchorCodec
{
    /// <summary>Serialises an anchor to UTF-8 JSON bytes. The time is written in UTC.</summary>
    /// <exception cref="ArgumentException">The record is not well formed.</exception>
    public static byte[] Serialize(AnchorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        AnchorRecord canonical = record with { AnchoredAt = record.AnchoredAt.ToUniversalTime() };
        if (!canonical.IsWellFormed)
        {
            throw new ArgumentException("Anchor record is not well formed.", nameof(record));
        }

        return JsonSerializer.SerializeToUtf8Bytes(canonical, AnchorJsonContext.Default.AnchorRecord);
    }

    /// <summary>Parses an anchor. Returns null for invalid JSON, unknown members, or out-of-range values; never throws on bad data.</summary>
    public static AnchorRecord? TryDeserialize(ReadOnlySpan<byte> json)
    {
        try
        {
            AnchorRecord? record = JsonSerializer.Deserialize(json, AnchorJsonContext.Default.AnchorRecord);
            return record is { IsWellFormed: true } ? record : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The canonical JSON as text, for logs and sink payloads.</summary>
    public static string SerializeToString(AnchorRecord record) => Encoding.UTF8.GetString(Serialize(record));
}
