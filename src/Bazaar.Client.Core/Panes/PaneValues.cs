using System.Text.Json;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Reads typed values from a pane model, raising <see cref="PaneModelException"/> on a shape mismatch.</summary>
internal static class PaneValues
{
    public static JsonElement Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new PaneModelException($"model field '{name}' must be an array");
        }

        return value;
    }

    public static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new PaneModelException($"model field '{name}' must be a string");
        }

        return value.GetString()!;
    }

    /// <summary>A string or number, shown as text. Null, objects and arrays are rejected.</summary>
    public static string Scalar(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new PaneModelException($"model field '{name}' is missing");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            _ => throw new PaneModelException($"model field '{name}' must be a scalar"),
        };
    }

    public static bool Flag(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
