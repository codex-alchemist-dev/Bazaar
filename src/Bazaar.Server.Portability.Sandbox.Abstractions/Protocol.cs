using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Portability.Sandbox;

/// <summary>
/// Request header sent on the child's stdin as one length-prefixed JSON frame, followed by exactly
/// <see cref="ContentLength"/> raw bytes of the staged source.
/// </summary>
public sealed record ParserRequest
{
    public const int CurrentProtocol = 1;

    public int Protocol { get; init; } = CurrentProtocol;
    public string Op { get; init; } = string.Empty;
    public string ImporterId { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public long ContentLength { get; init; }
    public long MaxSourceBytes { get; init; }
    public int MaxExpansionRatio { get; init; }
    public int MaxArchiveEntries { get; init; }
    public int MaxNestingDepth { get; init; }
    public int MaxRecordBytes { get; init; }
    public long MaxExpandedBytes { get; init; }

    public ImportLimits ToLimits() =>
        new(MaxSourceBytes, MaxExpansionRatio, MaxArchiveEntries, MaxNestingDepth, MaxRecordBytes, MaxExpandedBytes);

    public static ParserRequest For(string op, string importerId, string sourceName, long contentLength, ImportLimits limits) => new()
    {
        Op = op,
        ImporterId = importerId,
        SourceName = sourceName,
        ContentLength = contentLength,
        MaxSourceBytes = limits.MaxSourceBytes,
        MaxExpansionRatio = limits.MaxExpansionRatio,
        MaxArchiveEntries = limits.MaxArchiveEntries,
        MaxNestingDepth = limits.MaxNestingDepth,
        MaxRecordBytes = limits.MaxRecordBytes,
        MaxExpandedBytes = limits.MaxExpandedBytes,
    };
}

/// <summary>Operations a request can ask for.</summary>
public static class ParserOps
{
    public const string Detect = "detect";
    public const string Parse = "parse";
    public const string List = "list";
}

/// <summary>An importer the child can run.</summary>
public sealed record ParserImporterInfo(string Id, string Version);

/// <summary>Frame types the child writes on stdout.</summary>
public static class ParserMessageTypes
{
    public const string DetectResult = "detect";
    public const string Record = "record";
    public const string Importers = "importers";
    public const string Done = "done";
    public const string Error = "error";
}

/// <summary>Error classes the child reports.</summary>
public static class ParserErrorKinds
{
    public const string Limit = "limit";
    public const string Parser = "parser";
}

/// <summary>One response frame from the child. Only the fields of its <see cref="Type"/> are set.</summary>
public sealed record ParserMessage
{
    public string Type { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string? Reason { get; init; }
    public SourceRecord? Record { get; init; }
    public IReadOnlyList<ParserImporterInfo>? Importers { get; init; }
    public int Count { get; init; }
    public string? ErrorKind { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Source-generated JSON for the parser protocol, so the child and the server share one trim-safe format. These frames
/// are server-internal, not client wire messages, so they do not go through the <c>Envelope</c> payload registry of
/// <c>IWireCodec</c> in Bazaar.Contracts; they use the same System.Text.Json source-generation style.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ParserRequest))]
[JsonSerializable(typeof(ParserMessage))]
public sealed partial class ParserJsonContext : JsonSerializerContext;

/// <summary>Length-prefixed frames: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.</summary>
public static class ParserFrames
{
    /// <summary>The largest frame a reader accepts for records of <paramref name="maxRecordBytes"/> (JSON escaping can double a body).</summary>
    public static int MaxFrameBytes(int maxRecordBytes) =>
        (int)Math.Min(int.MaxValue, (2L * maxRecordBytes) + (1024 * 1024));

    public static void Write(Stream stream, ParserRequest request) =>
        WriteBytes(stream, JsonSerializer.SerializeToUtf8Bytes(request, ParserJsonContext.Default.ParserRequest));

    public static void Write(Stream stream, ParserMessage message) =>
        WriteBytes(stream, JsonSerializer.SerializeToUtf8Bytes(message, ParserJsonContext.Default.ParserMessage));

    /// <summary>Reads one frame body. Returns null at a clean end of stream. Throws on a frame over <paramref name="maxFrameBytes"/>.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        var header = new byte[4];
        var got = await ReadUpToAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new InvalidDataException("truncated frame header");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxFrameBytes) throw new FrameTooLargeException(length, maxFrameBytes);
        var body = new byte[length];
        if (await ReadUpToAsync(stream, body, ct).ConfigureAwait(false) < length)
            throw new InvalidDataException("truncated frame body");
        return body;
    }

    /// <summary>Reads and decodes one response frame, or null at a clean end of stream.</summary>
    public static async Task<ParserMessage?> ReadMessageAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        var body = await ReadFrameAsync(stream, maxFrameBytes, ct).ConfigureAwait(false);
        if (body is null) return null;
        try
        {
            return JsonSerializer.Deserialize(body, ParserJsonContext.Default.ParserMessage)
                ?? throw new InvalidDataException("empty message");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("frame is not valid JSON", ex);
        }
    }

    /// <summary>Reads and decodes the request header, or null at a clean end of stream.</summary>
    public static async Task<ParserRequest?> ReadRequestAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        var body = await ReadFrameAsync(stream, maxFrameBytes, ct).ConfigureAwait(false);
        if (body is null) return null;
        try
        {
            return JsonSerializer.Deserialize(body, ParserJsonContext.Default.ParserRequest)
                ?? throw new InvalidDataException("empty request");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("request is not valid JSON", ex);
        }
    }

    private static void WriteBytes(Stream stream, byte[] payload)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>A frame announced a length over the limit.</summary>
public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException(int length, int limit) : base($"frame of {length} bytes is over the limit of {limit}")
    {
        Length = length;
    }

    public FrameTooLargeException()
    {
    }

    public FrameTooLargeException(string message) : base(message)
    {
    }

    public FrameTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int Length { get; }
}
