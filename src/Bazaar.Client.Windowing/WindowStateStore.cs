using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Windowing;

/// <summary>
/// Saves the window list as one JSON document in an <see cref="IWindowStateFile"/>. <see cref="Load"/> never throws:
/// anything unreadable yields fewer windows, never an exception.
/// </summary>
/// <remarks>
/// Format: <c>{"v":1,"windows":[{"id","kind","title","bounds":{"x","y","width","height"},"state","monitorId","path","layout"}]}</c>.
/// Only these fields are written or read. Layout is handed to <see cref="DockJson"/>.
/// </remarks>
public sealed class WindowStateStore(IWindowStateFile file, IMonitorSource monitors)
{
    /// <summary>Format version written as <c>v</c>. Other versions load as empty.</summary>
    public const int Version = 1;

    /// <summary>Most windows <see cref="Load"/> returns.</summary>
    public const int MaxWindows = 32;

    /// <summary>Writes <paramref name="windows"/> as the complete window list, replacing any earlier save.</summary>
    public void Save(IReadOnlyList<WindowRecord> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        file.Write(Encode(windows));
    }

    /// <summary>
    /// Returns the saved windows that are valid. Invalid entries are skipped; duplicate ids keep the first; only one
    /// <see cref="WindowKind.Main"/> is kept; a minimized window loads as normal; bounds are fitted to a monitor; at
    /// most <see cref="MaxWindows"/> are returned. Returns an empty list when nothing usable is stored.
    /// </summary>
    public IReadOnlyList<WindowRecord> Load()
    {
        try
        {
            var text = file.Read();
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            return Decode(text, monitors.Monitors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException
                                       or InvalidOperationException or ArgumentException or KeyNotFoundException
                                       or InvalidCastException or IndexOutOfRangeException or NotSupportedException)
        {
            return [];
        }
    }

    private static string Encode(IReadOnlyList<WindowRecord> windows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteStartArray("windows");
            foreach (var window in windows)
            {
                writer.WriteStartObject();
                writer.WriteString("id", window.Id.Value);
                writer.WriteString("kind", window.Kind.ToString());
                writer.WriteString("title", window.Title);
                writer.WriteStartObject("bounds");
                writer.WriteNumber("x", window.Bounds.X);
                writer.WriteNumber("y", window.Bounds.Y);
                writer.WriteNumber("width", window.Bounds.Width);
                writer.WriteNumber("height", window.Bounds.Height);
                writer.WriteEndObject();
                writer.WriteString("state", window.State.ToString());
                WriteNullableString(writer, "monitorId", window.MonitorId);
                WriteNullableString(writer, "path", window.Path);
                writer.WritePropertyName("layout");
                writer.WriteRawValue(DockJson.Serialize(window.Layout));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static List<WindowRecord> Decode(string text, IReadOnlyList<MonitorInfo> monitorList)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number != Version
            || !root.TryGetProperty("windows", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<WindowRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var mainKept = false;
        foreach (var entry in entries.EnumerateArray())
        {
            if (result.Count >= MaxWindows)
            {
                break;
            }

            var record = ReadRecord(entry);
            if (record is null)
            {
                continue;
            }

            if (record.Kind == WindowKind.Main)
            {
                if (mainKept)
                {
                    continue;
                }

                mainKept = true;
            }

            if (!ids.Add(record.Id.Value))
            {
                continue;
            }

            var fitted = MonitorClamp.Clamp(record.Bounds, record.MonitorId, monitorList);
            result.Add(record with { Bounds = fitted });
        }

        return result;
    }

    private static WindowRecord? ReadRecord(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryReadString(entry, "id", out var id) || id.Length == 0
            || !TryReadKind(entry, out var kind)
            || !TryReadString(entry, "title", out var title)
            || !TryReadState(entry, out var state)
            || !TryReadBounds(entry, out var bounds)
            || !TryReadNullableString(entry, "monitorId", out var monitorId)
            || !TryReadNullableString(entry, "path", out var path)
            || !entry.TryGetProperty("layout", out var layoutElement))
        {
            return null;
        }

        var layout = layoutElement.ValueKind == JsonValueKind.Null
            ? null
            : DockJson.Deserialize(layoutElement.GetRawText());

        return new WindowRecord(new WindowId(id), kind, title, bounds, state, monitorId, path, layout);
    }

    private static bool TryReadString(JsonElement entry, string name, out string value)
    {
        if (entry.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadNullableString(JsonElement entry, string name, out string? value)
    {
        if (entry.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.Null)
            {
                value = null;
                return true;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryReadKind(JsonElement entry, out WindowKind kind)
    {
        if (TryReadString(entry, "kind", out var raw))
        {
            switch (raw)
            {
                case "Main":
                    kind = WindowKind.Main;
                    return true;
                case "Detached":
                    kind = WindowKind.Detached;
                    return true;
                case "Editor":
                    kind = WindowKind.Editor;
                    return true;
                case "Terminal":
                    kind = WindowKind.Terminal;
                    return true;
                case "Diff":
                    kind = WindowKind.Diff;
                    return true;
            }
        }

        kind = default;
        return false;
    }

    // Minimized windows come back as normal: a restore must never leave a window hidden.
    private static bool TryReadState(JsonElement entry, out WindowState state)
    {
        if (TryReadString(entry, "state", out var raw))
        {
            switch (raw)
            {
                case "Normal":
                case "Minimized":
                    state = WindowState.Normal;
                    return true;
                case "Maximized":
                    state = WindowState.Maximized;
                    return true;
            }
        }

        state = default;
        return false;
    }

    private static bool TryReadBounds(JsonElement entry, out WindowBounds bounds)
    {
        bounds = default;
        if (!entry.TryGetProperty("bounds", out var box) || box.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadInt(box, "x", out var x)
            || !TryReadInt(box, "y", out var y)
            || !TryReadInt(box, "width", out var width)
            || !TryReadInt(box, "height", out var height))
        {
            return false;
        }

        bounds = new WindowBounds(x, y, width, height);
        return true;
    }

    private static bool TryReadInt(JsonElement box, string name, out int value)
    {
        if (box.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
