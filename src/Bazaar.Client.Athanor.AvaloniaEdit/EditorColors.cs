using System.Text.Json;
using Avalonia.Media;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// The editor colours read from a Monaco defineTheme payload, so this host takes its palette from the same
/// <c>MonacoThemeGenerator</c> output as every other host (doc 57 §3). Missing or malformed colours stay null.
/// </summary>
public sealed record EditorColors(Color? Background, Color? Foreground, Color? Caret)
{
    /// <summary>Parses the payload. Throws <see cref="FormatException"/> when it is not a JSON object.</summary>
    public static EditorColors Parse(string monacoThemeJson)
    {
        ArgumentNullException.ThrowIfNull(monacoThemeJson);
        try
        {
            using JsonDocument document = JsonDocument.Parse(monacoThemeJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The theme payload is not a JSON object.");
            }

            if (!document.RootElement.TryGetProperty("colors", out JsonElement colors) || colors.ValueKind != JsonValueKind.Object)
            {
                return new EditorColors(null, null, null);
            }

            return new EditorColors(
                Read(colors, "editor.background"),
                Read(colors, "editor.foreground"),
                Read(colors, "editorCursor.foreground"));
        }
        catch (JsonException exception)
        {
            throw new FormatException("The theme payload is not valid JSON.", exception);
        }
    }

    private static Color? Read(JsonElement colors, string name) =>
        colors.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && Color.TryParse(value.GetString(), out Color color)
            ? color
            : null;
}
