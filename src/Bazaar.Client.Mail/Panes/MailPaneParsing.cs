using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>
/// Reads the mail pane models. Each required field that is missing or has the wrong shape raises
/// <see cref="PaneModelException"/>, so the registry falls back to the generic view. Fields the models do not name are
/// ignored, which keeps a body or HTML that the server never meant to send out of the screen.
/// </summary>
internal static class MailPaneParsing
{
    public static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException($"model field '{name}' must be an object");
        }

        return value;
    }

    /// <summary>A required array of strings. Every item must be a string.</summary>
    public static IReadOnlyList<string> Strings(JsonElement parent, string name)
    {
        var result = new List<string>();
        foreach (var item in PaneValues.Array(parent, name).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new PaneModelException($"'{name}' must hold strings");
            }

            result.Add(item.GetString()!);
        }

        return result;
    }

    /// <summary>A required non-negative count.</summary>
    public static int Count(JsonElement parent, string name)
    {
        var value = PaneValues.RequiredInt(parent, name);
        return value >= 0 ? value : throw new PaneModelException($"model field '{name}' must not be negative");
    }

    /// <summary>A required date-time in ISO 8601 form.</summary>
    public static DateTimeOffset Date(JsonElement parent, string name)
    {
        var text = PaneValues.RequiredString(parent, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : throw new PaneModelException($"model field '{name}' must be a date-time");
    }

    /// <summary>An optional string. A missing field or null reads as null; any other type is rejected.</summary>
    public static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new PaneModelException($"model field '{name}' must be a string or null");
    }
}
