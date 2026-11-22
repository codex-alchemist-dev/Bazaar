using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// Where a streaming exporter writes its files (doc 44 sections 4.1 and 5). The bundle writer implements it, so every byte is
/// hashed as it is written. An exporter opens one file at a time, writes it, and disposes the stream. Paths are bundle-relative,
/// use forward slashes, and never start with a slash.
/// </summary>
public interface IExportFileSink
{
    /// <summary>Opens a new file. The caller writes it and disposes the stream; the sink keeps the bytes.</summary>
    Task<Stream> OpenAsync(string path, CancellationToken ct);
}

/// <summary>Shared text rules for the exporters: UTF-8 without a byte order mark, LF line endings, UTC timestamps (doc 44 section 4.1).</summary>
internal static class ExportText
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    private const string _timestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>A timestamp as UTC text with a fixed shape, so the same instant always gives the same bytes.</summary>
    internal static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(_timestampFormat, CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes one JSON object per line (JSON Lines). Each record is built in a reusable buffer and then written, so memory stays
/// flat however many records a file holds.
/// </summary>
internal sealed class JsonLineWriter(Stream output)
{
    private static readonly byte[] _lf = [(byte)'\n'];

    private readonly ArrayBufferWriter<byte> _buffer = new();

    public async Task WriteAsync(Action<Utf8JsonWriter> writeRecord, CancellationToken ct)
    {
        _buffer.ResetWrittenCount();
        using (var json = new Utf8JsonWriter(_buffer))
        {
            writeRecord(json);
        }

        await output.WriteAsync(_buffer.WrittenMemory, ct);
        await output.WriteAsync(_lf, ct);
    }
}
