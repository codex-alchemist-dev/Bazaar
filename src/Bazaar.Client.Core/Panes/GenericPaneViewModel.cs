using System.Text.Json;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The fallback view for any pane kind. Flattens the model into indented "key: value" lines, a plain tree
/// view that works for every JSON shape. Output is capped so a large model cannot stall the UI.
/// </summary>
public sealed class GenericPaneViewModel : IPaneViewModel
{
    /// <summary>Upper bound on rendered lines.</summary>
    public const int MaxLines = 500;

    /// <summary>Deepest nesting level rendered. Deeper values are shown as "...".</summary>
    public const int MaxDepth = 8;

    public GenericPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = descriptor.Kind;
        Title = descriptor.Title;

        var lines = new List<string>();
        Flatten(descriptor.Model, lines, name: null, depth: 0);
        if (lines.Count > MaxLines)
        {
            lines.RemoveRange(MaxLines, lines.Count - MaxLines);
            lines.Add("...");
        }

        Lines = lines;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>Indented lines, two spaces per level.</summary>
    public IReadOnlyList<string> Lines { get; }

    private static void Flatten(JsonElement element, List<string> lines, string? name, int depth)
    {
        if (lines.Count > MaxLines)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        var label = name is null ? string.Empty : name + ": ";

        if (depth > MaxDepth)
        {
            lines.Add(indent + label + "...");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                lines.Add(indent + (name is null ? "{" : name + ":"));
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, lines, property.Name, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                lines.Add(indent + (name is null ? "[" : name + ":"));
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, lines, $"[{index}]", depth + 1);
                    index++;
                }

                break;
            case JsonValueKind.String:
                lines.Add(indent + label + element.GetString());
                break;
            default:
                lines.Add(indent + label + element.GetRawText());
                break;
        }
    }
}
