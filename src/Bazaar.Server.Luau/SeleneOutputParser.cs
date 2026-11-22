using System.Text.Json;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Parses Selene's <c>--display-style=Json2</c> output: one JSON object per diagnostic, one per line. Assumed fields
/// (not verified against a recorded binary, see the fixtures): <c>code</c>, <c>level</c> (<c>error</c>, <c>warning</c>,
/// other values map to Info), <c>message</c>, and <c>primary_label</c> with <c>filename</c> and
/// <c>span.start_line</c> / <c>span.start_column</c>. Lines that are not JSON objects, and objects missing fields, are skipped.
/// </summary>
public static class SeleneOutputParser
{
    /// <summary>Parses the whole output. An empty or unrecognised output yields no diagnostics.</summary>
    public static IReadOnlyList<LuauDiagnostic> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var diagnostics = new List<LuauDiagnostic>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            LuauDiagnostic? diagnostic = ParseLine(line);
            if (diagnostic is not null)
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics;
    }

    private static LuauDiagnostic? ParseLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out JsonElement messageElement))
            {
                return null;
            }

            string code = StringProperty(root, "code");
            string level = StringProperty(root, "level");
            string file = string.Empty;
            int lineNumber = 0;
            int column = 0;
            if (root.TryGetProperty("primary_label", out JsonElement label) && label.ValueKind == JsonValueKind.Object)
            {
                file = StringProperty(label, "filename");
                if (label.TryGetProperty("span", out JsonElement span) && span.ValueKind == JsonValueKind.Object)
                {
                    lineNumber = IntProperty(span, "start_line");
                    column = IntProperty(span, "start_column");
                }
            }

            return new LuauDiagnostic(
                LuauToolKind.Selene,
                file,
                lineNumber,
                column,
                SeverityFor(level),
                code,
                messageElement.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LuauSeverity SeverityFor(string level) => level.ToLowerInvariant() switch
    {
        "error" => LuauSeverity.Error,
        "warning" => LuauSeverity.Warning,
        _ => LuauSeverity.Info,
    };

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int IntProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
}
