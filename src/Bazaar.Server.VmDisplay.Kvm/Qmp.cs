using System.Text;
using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>The kind of one QMP <c>InputEvent</c>.</summary>
internal enum QmpEventKind
{
    Key,
    Btn,
    Abs,
}

/// <summary>One QMP <c>InputEvent</c>: a key (qcode), a pointer button, or an absolute axis value.</summary>
internal readonly record struct QmpEvent(QmpEventKind Kind, string Name, bool Down, int Value)
{
    public static QmpEvent Key(string qcode, bool down) => new(QmpEventKind.Key, qcode, down, 0);

    public static QmpEvent Btn(string button, bool down) => new(QmpEventKind.Btn, button, down, 0);

    public static QmpEvent Abs(string axis, int value) => new(QmpEventKind.Abs, axis, false, value);
}

/// <summary>The decoded outcome of one QMP response object.</summary>
internal sealed record QmpResult(bool Ok, string? ErrorDescription)
{
    /// <summary>Parses a response object. A response with neither <c>return</c> nor <c>error</c> is a failure.</summary>
    public static QmpResult Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DisplayException(DisplayFailure.SourceFailed, "The QMP response is not an object.");
            if (root.TryGetProperty("error", out JsonElement error))
            {
                string? desc = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("desc", out JsonElement d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                return new QmpResult(false, desc ?? "unknown QMP error");
            }

            if (root.TryGetProperty("return", out _))
                return new QmpResult(true, null);
            throw new DisplayException(DisplayFailure.SourceFailed, "The QMP response has neither return nor error.");
        }
        catch (JsonException e)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, "The QMP response is not valid JSON.", e);
        }
    }
}

/// <summary>Builds QMP command text. JSON is written with <see cref="Utf8JsonWriter"/>, never concatenated.</summary>
internal static class QmpCommands
{
    /// <summary>The upper end of QMP's absolute axis range (axes run 0 to 32767).</summary>
    public const int AbsMax = 32767;

    /// <summary>Most events in one <c>input-send-event</c> command.</summary>
    public const int MaxEventsPerCommand = 64;

    /// <summary><c>screendump</c> to <paramref name="filename"/>. <paramref name="format"/> is omitted when null (QEMU older than 7.1).</summary>
    public static string Screendump(string filename, string? format)
    {
        return Build(w =>
        {
            w.WriteString("execute", "screendump");
            w.WriteStartObject("arguments");
            w.WriteString("filename", filename);
            if (format is not null)
                w.WriteString("format", format);
            w.WriteEndObject();
        });
    }

    /// <summary>One <c>input-send-event</c> carrying <paramref name="events"/>.</summary>
    public static string InputSendEvent(IReadOnlyList<QmpEvent> events)
    {
        return Build(w =>
        {
            w.WriteString("execute", "input-send-event");
            w.WriteStartObject("arguments");
            w.WriteStartArray("events");
            foreach (QmpEvent e in events)
                WriteEvent(w, e);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static void WriteEvent(Utf8JsonWriter w, QmpEvent e)
    {
        w.WriteStartObject();
        switch (e.Kind)
        {
            case QmpEventKind.Key:
                w.WriteString("type", "key");
                w.WriteStartObject("data");
                w.WriteBoolean("down", e.Down);
                w.WriteStartObject("key");
                w.WriteString("type", "qcode");
                w.WriteString("data", e.Name);
                w.WriteEndObject();
                w.WriteEndObject();
                break;
            case QmpEventKind.Btn:
                w.WriteString("type", "btn");
                w.WriteStartObject("data");
                w.WriteBoolean("down", e.Down);
                w.WriteString("button", e.Name);
                w.WriteEndObject();
                break;
            default:
                w.WriteString("type", "abs");
                w.WriteStartObject("data");
                w.WriteString("axis", e.Name);
                w.WriteNumber("value", e.Value);
                w.WriteEndObject();
                break;
        }

        w.WriteEndObject();
    }

    private static string Build(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}

/// <summary>Reads the pixel size from the header of a PNG or binary PPM file, without decoding it.</summary>
internal static class ImageHeader
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Whether <paramref name="data"/> starts with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

    /// <summary>Reads width and height from a PNG <c>IHDR</c> chunk.</summary>
    public static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        // signature(8) + length(4) + "IHDR"(4) + width(4) + height(4)
        if (!IsPng(data) || data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return false;
        uint w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        uint h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
            return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>Reads width and height from a binary PPM (P6) header.</summary>
    public static bool TryReadPpm(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 3 || data[0] != (byte)'P' || data[1] != (byte)'6')
            return false;
        int pos = 2;
        return TryToken(data, ref pos, out width) && TryToken(data, ref pos, out height) && width > 0 && height > 0;
    }

    private static bool TryToken(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        value = 0;
        while (pos < data.Length && (data[pos] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
            pos++;
        int start = pos;
        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9' && pos - start < 9)
        {
            value = (value * 10) + (data[pos] - (byte)'0');
            pos++;
        }

        return pos > start;
    }
}
