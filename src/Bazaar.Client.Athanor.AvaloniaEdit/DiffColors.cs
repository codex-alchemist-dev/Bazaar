using System.Text.Json;
using Avalonia.Media;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// The added and removed line backgrounds for the diff view, taken from the Monaco theme JSON (doc 57 §2). The theme's
/// <c>diffEditor.insertedLineBackground</c> and <c>diffEditor.removedLineBackground</c> win when present; otherwise green
/// and red are blended over the editor background, stronger on a dark base. The result is always opaque.
/// </summary>
public sealed record DiffColors(Color Added, Color Removed, Color Filler)
{
    private static readonly Color _green = Color.FromRgb(0x2E, 0xA0, 0x43);
    private static readonly Color _red = Color.FromRgb(0xF8, 0x51, 0x49);

    /// <summary>The colours used before any theme is pushed (dark neutral).</summary>
    public static DiffColors Default { get; } = Derive(Color.FromRgb(0x1E, 0x1E, 0x1E), dark: true);

    /// <summary>Reads the colours. Throws <see cref="FormatException"/> when the payload is not a JSON object.</summary>
    public static DiffColors Parse(string monacoThemeJson)
    {
        ArgumentNullException.ThrowIfNull(monacoThemeJson);
        try
        {
            using JsonDocument document = JsonDocument.Parse(monacoThemeJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The theme payload is not a JSON object.");
            }

            bool dark = !root.TryGetProperty("base", out JsonElement baseElement)
                || baseElement.ValueKind != JsonValueKind.String
                || baseElement.GetString() != "vs";
            JsonElement colors = root.TryGetProperty("colors", out JsonElement c) && c.ValueKind == JsonValueKind.Object ? c : default;
            Color background = Read(colors, "editor.background") ?? (dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White);
            DiffColors derived = Derive(background, dark);
            return new DiffColors(
                Flatten(Read(colors, "diffEditor.insertedLineBackground"), background) ?? derived.Added,
                Flatten(Read(colors, "diffEditor.removedLineBackground"), background) ?? derived.Removed,
                derived.Filler);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The theme payload is not valid JSON.", exception);
        }
    }

    private static DiffColors Derive(Color background, bool dark)
    {
        double strength = dark ? 0.28 : 0.20;
        return new DiffColors(Blend(_green, background, strength), Blend(_red, background, strength), Blend(Colors.Gray, background, 0.10));
    }

    private static Color Blend(Color top, Color bottom, double alpha) =>
        Color.FromRgb(
            (byte)Math.Round((top.R * alpha) + (bottom.R * (1 - alpha))),
            (byte)Math.Round((top.G * alpha) + (bottom.G * (1 - alpha))),
            (byte)Math.Round((top.B * alpha) + (bottom.B * (1 - alpha))));

    private static Color? Flatten(Color? colour, Color background) =>
        colour is { } value ? Blend(Color.FromRgb(value.R, value.G, value.B), background, value.A / 255.0) : null;

    private static Color? Read(JsonElement colors, string name) =>
        colors.ValueKind == JsonValueKind.Object
        && colors.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && TryMonacoColor(value.GetString(), out Color color)
            ? color
            : null;

    // Monaco writes #RRGGBBAA; Avalonia reads #AARRGGBB.
    private static bool TryMonacoColor(string? text, out Color color)
    {
        if (text is { Length: 9 } && text[0] == '#')
        {
            return Color.TryParse("#" + text[7..] + text[1..7], out color);
        }

        return Color.TryParse(text, out color);
    }
}
