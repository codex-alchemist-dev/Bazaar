using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Athanor.Gtk.Protocol;

/// <summary>
/// The control layer of the helper pipe (doc 57 section 2.1): one JSON object per line whose <c>type</c> starts with
/// <c>helper.</c>. The bridge messages of doc 57 section 4 travel on the same lines and are never interpreted here.
/// This file is compiled into both the helper and the proxy (a linked source file), so the two cannot drift.
/// </summary>
internal static class HelperProtocol
{
    /// <summary>The control protocol version the helper reports in <c>helper.hello</c>.</summary>
    public const int Version = 1;

    /// <summary>The prefix of every control message type.</summary>
    public const string TypePrefix = "helper.";

    public const string Hello = "helper.hello";
    public const string Pong = "helper.pong";
    public const string WindowClosed = "helper.windowClosed";
    public const string Fatal = "helper.fatal";
    public const string Window = "helper.window";
    public const string Ping = "helper.ping";
    public const string Shutdown = "helper.shutdown";

    /// <summary>Reads the <c>type</c> of a line. False when the line is not a JSON object with a string <c>type</c>.</summary>
    public static bool TryGetType(string line, out string type)
    {
        type = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out JsonElement t)
                || t.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            type = t.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsControl(string type) => type.StartsWith(TypePrefix, StringComparison.Ordinal);

    public static string BuildHello(string webkitVersion, bool sandbox, int pid) =>
        Build(Hello, w =>
        {
            w.WriteNumber("protocol", Version);
            w.WriteString("webkit", webkitVersion);
            w.WriteBoolean("sandbox", sandbox);
            w.WriteNumber("pid", pid);
        });

    public static string BuildPong(long id) => Build(Pong, w => w.WriteNumber("id", id));

    public static string BuildPing(long id) => Build(Ping, w => w.WriteNumber("id", id));

    public static string BuildWindowClosed() => Build(WindowClosed, _ => { });

    public static string BuildShutdown() => Build(Shutdown, _ => { });

    public static string BuildFatal(string code, string message) =>
        Build(Fatal, w =>
        {
            w.WriteString("code", code);
            w.WriteString("message", message);
        });

    public static string BuildWindow(int x, int y, int width, int height, bool visible, string? title) =>
        Build(Window, w =>
        {
            w.WriteNumber("x", x);
            w.WriteNumber("y", y);
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteBoolean("visible", visible);
            if (title is not null)
            {
                w.WriteString("title", title);
            }
        });

    /// <summary>Re-emits a JSON text as one compact line (no raw newline). Throws <see cref="JsonException"/> on malformed input.</summary>
    public static string Compact(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Build(string type, Action<Utf8JsonWriter> fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            fields(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
