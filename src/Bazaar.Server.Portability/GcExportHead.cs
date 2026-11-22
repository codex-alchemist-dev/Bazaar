using System.Buffers;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// <c>head.json</c> for one GC export (doc 44 section 4.2): the head seq the export was taken at, the row counts, the head hash of
/// each chain, and the SHA-256 and size of each file. The self-check reads it back and compares it with the files it describes.
/// </summary>
/// <remarks>
/// <c>head.json</c> is not itself chained. It names the other files, and the bundle manifest hashes it (doc 44 section 5). It carries
/// no export time, so the same log always gives the same bytes.
/// </remarks>
public sealed record GcExportHead(
    Guid GcId,
    long HeadSeq,
    long EventCount,
    long MessageCount,
    long VersionCount,
    string EventHeadHash,
    string MessageHeadHash,
    string VersionHeadHash,
    IReadOnlyList<ExportedFile> Files)
{
    /// <summary>Canonical head.json bytes: fixed field order, compact, LF at the end.</summary>
    public byte[] ToBytes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, GcJsonlFormat.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("formatId", GcJsonlFormat.FormatId);
            writer.WriteString("schemaVersion", GcJsonlFormat.SchemaVersion);
            writer.WriteString("gcId", GcId.ToString("D"));
            writer.WriteNumber("headSeq", HeadSeq);
            writer.WriteNumber("eventCount", EventCount);
            writer.WriteNumber("messageCount", MessageCount);
            writer.WriteNumber("versionCount", VersionCount);
            writer.WriteString("eventHeadHash", EventHeadHash);
            writer.WriteString("messageHeadHash", MessageHeadHash);
            writer.WriteString("versionHeadHash", VersionHeadHash);
            writer.WriteStartObject("files");
            foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject(file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("bytes", file.Bytes);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var bytes = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    /// <summary>Reads head.json back. Returns null with a reason when the bytes are not a head this format wrote.</summary>
    public static GcExportHead? TryParse(ReadOnlyMemory<byte> bytes, out string? problem)
    {
        problem = null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (Text(root, "formatId") != GcJsonlFormat.FormatId)
            {
                problem = "head.json is not a " + GcJsonlFormat.FormatId + " head";
                return null;
            }

            var files = new List<ExportedFile>();
            foreach (var property in root.GetProperty("files").EnumerateObject())
            {
                files.Add(new ExportedFile(property.Name, Text(property.Value, "sha256"), property.Value.GetProperty("bytes").GetInt64()));
            }

            return new GcExportHead(
                Guid.Parse(Text(root, "gcId")),
                root.GetProperty("headSeq").GetInt64(),
                root.GetProperty("eventCount").GetInt64(),
                root.GetProperty("messageCount").GetInt64(),
                root.GetProperty("versionCount").GetInt64(),
                Text(root, "eventHeadHash"),
                Text(root, "messageHeadHash"),
                Text(root, "versionHeadHash"),
                files);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problem = "head.json is malformed: " + ex.Message;
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new FormatException(name + " is null");
}
