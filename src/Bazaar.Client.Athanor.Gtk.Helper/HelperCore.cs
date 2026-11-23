using System.Text.Json;
using Bazaar.Client.Athanor.Gtk.Protocol;

namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>
/// The helper's logic without any GTK: reads lines from the client, answers control messages, relays bridge messages
/// to the page and the page's messages back (doc 57 section 2.1). The helper never interprets bridge messages; the
/// proxy's codec and sequence guard do that.
/// </summary>
public sealed class HelperCore
{
    private readonly IEditorSurface _surface;
    private readonly TextWriter _output;
    private readonly object _outputLock = new();
    private readonly long _maxLineChars;

    /// <summary>Creates the core. <paramref name="maxLineChars"/> bounds one line from either side.</summary>
    public HelperCore(IEditorSurface surface, TextWriter output, long maxLineChars = 16L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(output);
        _surface = surface;
        _output = output;
        _maxLineChars = maxLineChars;
    }

    /// <summary>Writes <c>helper.hello</c>, the first line of every session.</summary>
    public void SendHello(string webkitVersion, bool sandbox, int pid) =>
        WriteLine(HelperProtocol.BuildHello(webkitVersion, sandbox, pid));

    /// <summary>A bridge message from the page. Compacted to one line and passed to the client.</summary>
    public void OnPageMessage(string json)
    {
        if (json is null || json.Length > _maxLineChars)
        {
            ReportFatal("page_message_too_large", "The page sent a message over the size limit.");
            return;
        }

        try
        {
            WriteLine(HelperProtocol.Compact(json));
        }
        catch (JsonException)
        {
            ReportFatal("page_message_malformed", "The page sent a message that is not JSON.");
        }
    }

    /// <summary>The user closed the editor window (it is hidden, not destroyed).</summary>
    public void OnWindowClosed() => WriteLine(HelperProtocol.BuildWindowClosed());

    /// <summary>The page or its process failed. Tells the client, then ends the UI loop.</summary>
    public void ReportFatal(string code, string message)
    {
        WriteLine(HelperProtocol.BuildFatal(code, message));
        _surface.Quit();
    }

    /// <summary>
    /// Reads the client's lines until end of input, which means the client is gone or finished: the UI loop then ends.
    /// Runs on its own thread, and marshals each line to the UI thread through the surface.
    /// </summary>
    public void RunInputLoop(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                string captured = line;
                _surface.Dispatch(() => HandleLine(captured));
            }
        }
        catch (IOException)
        {
            // The pipe broke: treated the same as end of input.
        }

        _surface.Dispatch(_surface.Quit);
    }

    /// <summary>Handles one line from the client. Runs on the UI thread.</summary>
    public void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Length > _maxLineChars)
        {
            ReportFatal("line_too_large", "The client sent a line over the size limit.");
            return;
        }

        if (!HelperProtocol.TryGetType(line, out string type))
        {
            ReportFatal("line_malformed", "The client sent a line that is not a JSON object with a type.");
            return;
        }

        if (!HelperProtocol.IsControl(type))
        {
            // A bridge message: the JSON text goes to the page as a literal and is never built into script from user text.
            _surface.PostToPage(line);
            return;
        }

        switch (type)
        {
            case HelperProtocol.Ping:
                WriteLine(HelperProtocol.BuildPong(ReadLong(line, "id")));
                break;
            case HelperProtocol.Window:
                _surface.ApplyWindow(ReadPlacement(line));
                break;
            case HelperProtocol.Shutdown:
                _surface.Quit();
                break;
            default:
                ReportFatal("control_unknown", "The client sent an unknown control message.");
                break;
        }
    }

    private void WriteLine(string line)
    {
        lock (_outputLock)
        {
            _output.Write(line);
            _output.Write('\n');
            _output.Flush();
        }
    }

    private static long ReadLong(string line, string name)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty(name, out JsonElement e) && e.TryGetInt64(out long value) ? value : 0;
    }

    private static WindowPlacement ReadPlacement(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        int Int(string name, int fallback) => root.TryGetProperty(name, out JsonElement e) && e.TryGetInt32(out int v) ? v : fallback;
        bool visible = !root.TryGetProperty("visible", out JsonElement vis) || vis.ValueKind != JsonValueKind.False;
        string? title = root.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return new WindowPlacement(
            Int("x", 0),
            Int("y", 0),
            Math.Clamp(Int("width", 1100), 100, 20000),
            Math.Clamp(Int("height", 700), 100, 20000),
            visible,
            title);
    }
}
