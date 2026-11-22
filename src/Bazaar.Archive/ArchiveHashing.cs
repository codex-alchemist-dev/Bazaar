using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Archive;

/// <summary>Hashing and the JSON Lines form, in one place so archive, verify and restore agree.</summary>
public static class ArchiveHashing
{
    public const int FormatVersion = 1;
    public const string ManifestName = "manifest.json";

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string RootHash(IEnumerable<ArchiveSegment> segments)
    {
        var joined = string.Concat(segments.Select(s => s.Sha256));
        return Sha256Hex(Encoding.UTF8.GetBytes(joined));
    }

    /// <summary>The canonical line for a record: one JSON object, no line breaks.</summary>
    public static string ToLine(ArchiveRecord r) => JsonSerializer.Serialize(new Line(r.Id, r.Kind, r.At.ToUniversalTime().ToString("O"), r.Body), ArchiveJson.Default.Line);

    public static ArchiveRecord FromLine(string line)
    {
        var l = JsonSerializer.Deserialize(line, ArchiveJson.Default.Line) ?? throw new FormatException("Empty record line.");
        return new ArchiveRecord(l.Id, l.Kind, DateTimeOffset.Parse(l.At, null, System.Globalization.DateTimeStyles.RoundtripKind), l.Body);
    }

    /// <summary>The hash of a record's canonical line, used to compare a live record with an archived one.</summary>
    public static string RecordHash(ArchiveRecord r) => Sha256Hex(Encoding.UTF8.GetBytes(ToLine(r)));

    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.') && !id.StartsWith('.');

    internal sealed record Line(string Id, string Kind, string At, string Body);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(ArchiveHashing.Line))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ArchiveManifest))]
internal sealed partial class ArchiveJson : System.Text.Json.Serialization.JsonSerializerContext;
