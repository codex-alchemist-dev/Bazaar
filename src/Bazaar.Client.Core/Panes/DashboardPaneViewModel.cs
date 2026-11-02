using System.Text.Json;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>A labelled figure on the dashboard, for example "Unread GCs" with "3".</summary>
public sealed record DashboardItem(string Label, string Value);

/// <summary>A titled group of items, for example "Awaiting you".</summary>
public sealed record DashboardSection(string Title, IReadOnlyList<DashboardItem> Items);

/// <summary>
/// Start-screen dashboard of what changed while away. Model:
/// <c>{ "sections": [ { "title": "...", "items": [ { "label": "...", "value": "..." } ] } ] }</c>.
/// The server decides what each section holds; the client only lays it out.
/// </summary>
public sealed class DashboardPaneViewModel : IPaneViewModel
{
    public DashboardPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = descriptor.Kind;
        Title = descriptor.Title;
        Sections = PaneValues.Array(descriptor.Model, "sections")
            .EnumerateArray()
            .Select(ReadSection)
            .ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    public IReadOnlyList<DashboardSection> Sections { get; }

    private static DashboardSection ReadSection(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each dashboard section must be an object");
        }

        var items = PaneValues.Array(section, "items")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object
                ? new DashboardItem(PaneValues.RequiredString(item, "label"), PaneValues.Scalar(item, "value"))
                : throw new PaneModelException("each dashboard item must be an object"))
            .ToArray();

        return new DashboardSection(PaneValues.RequiredString(section, "title"), items);
    }
}
