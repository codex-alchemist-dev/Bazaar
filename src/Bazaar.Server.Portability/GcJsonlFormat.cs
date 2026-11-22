using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Portability;

/// <summary>
/// The <c>gc.jsonl.v1</c> layout and line format (doc 44 sections 4.1 and 4.2). The exporter writes these lines and the self-check
/// reads them back, so both use this one definition.
/// </summary>
/// <remarks>
/// Every line is canonical JSON: fields in a fixed order, no insignificant whitespace, UTF-8, LF line endings, and object keys
/// sorted inside payloads. Each line ends with a hash chain. <c>prevHash</c> is the <c>hash</c> of the line before it (64 zeros for
/// the first line of a file). <c>hash</c> is SHA-256 over the line's bytes up to and including <c>prevHash</c>. The GC log contracts
/// carry no event hash, so the chain is computed here and belongs to this export.
/// </remarks>
public static class GcJsonlFormat
{
    public const string FormatId = "gc.jsonl.v1";
    public const string SchemaVersion = "1.0.0";
    public const string EventsFile = "events.jsonl";
    public const string MessagesFile = "messages.jsonl";
    public const string VersionsFile = "versions.jsonl";
    public const string HeadFile = "head.json";

    /// <summary>The previous hash of the first line in each file, and the head hash of an empty file.</summary>
    public static readonly string GenesisHash = new('0', _hashLength);

    /// <summary>Canonical JSON options: compact output, with relaxed escaping so non-ASCII text stays readable.</summary>
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int _hashLength = 64;
    private static readonly string _hashFieldStart = ",\"hash\":\"";
    // The hash field and the closing brace: the fixed-length suffix every sealed line ends with.
    private static readonly int _suffixLength = _hashFieldStart.Length + _hashLength + "\"}".Length;

    /// <summary>Path of a file inside the export for one GC (doc 44 section 4.2).</summary>
    public static string PathFor(Guid gcId, string file) => "gc/" + gcId.ToString("D") + "/" + file;

    /// <summary>The UTC timestamp form every line uses: fixed width, 100 ns precision.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The wire name of an author kind. Written out by hand so the format never depends on reflection.</summary>
    public static string WireName(AuthorKind kind) => kind switch
    {
        AuthorKind.Owner => "owner",
        AuthorKind.Human => "human",
        AuthorKind.Agent => "agent",
        AuthorKind.System => "system",
        AuthorKind.ExternalBridge => "external_bridge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown author kind."),
    };

    /// <summary>The wire name of a source trust level (doc 15 enum).</summary>
    public static string WireName(SourceTrust trust) => trust switch
    {
        SourceTrust.Owner => "owner",
        SourceTrust.System => "system",
        SourceTrust.MemberHuman => "member-human",
        SourceTrust.Agent => "agent",
        SourceTrust.External => "external",
        SourceTrust.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown source trust."),
    };

    /// <summary>
    /// Writes one sealed line: the fields from <paramref name="writeFields"/>, then <c>prevHash</c>, then <c>hash</c>, then LF.
    /// Nothing is returned when <paramref name="writeFields"/> throws.
    /// </summary>
    internal static SealedLine Seal(string prevHash, Action<Utf8JsonWriter> writeFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writeFields(writer);
            writer.WriteString("prevHash", prevHash);
            writer.WriteEndObject();
        }

        // The closing brace goes: the hash field follows the body, and the hash covers the body.
        var body = buffer.WrittenSpan[..^1];
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var suffix = Encoding.ASCII.GetBytes(_hashFieldStart + hash + "\"}\n");
        var line = new byte[body.Length + suffix.Length];
        body.CopyTo(line);
        suffix.CopyTo(line.AsSpan(body.Length));
        return new SealedLine(line, hash);
    }

    /// <summary>
    /// Checks one line read back from a file. The line's hash must match its content, and its <c>prevHash</c> must be
    /// <paramref name="expectedPrevHash"/>. Returns the problem, or null when the line is sound. <paramref name="document"/> is the
    /// parsed line when it parses (the caller disposes it). <paramref name="storedHash"/> is the hash the line carries, or empty.
    /// </summary>
    internal static string? CheckLine(string line, string expectedPrevHash, out JsonDocument? document, out string storedHash)
    {
        document = null;
        storedHash = string.Empty;
        var start = line.Length - _suffixLength;
        if (start < 0 || string.CompareOrdinal(line, start, _hashFieldStart, 0, _hashFieldStart.Length) != 0 ||
            !line.EndsWith("\"}", StringComparison.Ordinal))
        {
            return "the line does not end with a hash field";
        }

        storedHash = line.Substring(start + _hashFieldStart.Length, _hashLength);
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return "the line is not valid JSON";
        }

        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line[..start]))).ToLowerInvariant();
        if (computed != storedHash)
        {
            return "the hash does not match the line content";
        }

        if (!document.RootElement.TryGetProperty("prevHash", out var prev) || prev.GetString() != expectedPrevHash)
        {
            return "prevHash does not chain to the line before it";
        }

        return null;
    }

    /// <summary>
    /// Writes a property whose value is opaque JSON text (a payload or a taint list) in canonical form, or null. Object keys are
    /// sorted, and a duplicate key is refused, so the same payload always gives the same bytes.
    /// </summary>
    internal static void WriteCanonical(Utf8JsonWriter writer, string name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(json);
        WriteElement(writer, document.RootElement);
    }

    /// <summary>Reads a stream to its end in chunks and returns its name, SHA-256 and size. The file is never held whole.</summary>
    internal static async Task<ExportedFile> HashAsync(string path, Stream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return new ExportedFile(path, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
    }

    /// <summary>Writes a nullable GUID as a string, or null.</summary>
    internal static void WriteOptional(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(name, id.ToString("D"));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>Writes a nullable number, or null.</summary>
    internal static void WriteOptional(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                    for (var i = 1; i < properties.Count; i++)
                    {
                        if (properties[i - 1].Name == properties[i].Name)
                        {
                            throw new InvalidDataException("Duplicate key in a payload: " + properties[i].Name);
                        }
                    }

                    writer.WriteStartObject();
                    foreach (var property in properties)
                    {
                        writer.WritePropertyName(property.Name);
                        WriteElement(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;
                }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Unsupported JSON value in a payload.");
        }
    }
}

/// <summary>A sealed line: its bytes, LF included, and the hash it carries.</summary>
internal sealed record SealedLine(byte[] Bytes, string Hash);
