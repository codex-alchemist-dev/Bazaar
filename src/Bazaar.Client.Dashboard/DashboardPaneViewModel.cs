using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Dashboard;

/// <summary>A labelled figure on the dashboard, for example "Unread GCs" with "3".</summary>
public sealed record DashboardItem(string Label, string Value)
{
    /// <summary>The value as a count when it is a whole number of zero or more, for example "3"; otherwise null. A count shows as a badge.</summary>
    public int? Count => int.TryParse(Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}

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

    public IReadOnlyList<DashboardSection> Sections { get; private set; }

    /// <summary>Raised after <see cref="Update"/> changed a figure, with the section title and the item label.</summary>
    public event EventHandler<DashboardItemChanged>? ItemChanged;

    /// <summary>
    /// Sets one figure to a new value, for example when the server's "since last seen" figures move. Returns false when the section or
    /// item is not on the dashboard or the value is the same. Call on the UI thread.
    /// </summary>
    public bool Update(string sectionTitle, string label, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sections = Sections.ToArray();
        for (var s = 0; s < sections.Length; s++)
        {
            if (sections[s].Title != sectionTitle) continue;
            var items = sections[s].Items.ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Label != label) continue;
                if (items[i].Value == value) return false;
                items[i] = items[i] with { Value = value };
                sections[s] = sections[s] with { Items = items };
                Sections = sections;
                ItemChanged?.Invoke(this, new DashboardItemChanged(sectionTitle, items[i]));
                return true;
            }
        }

        return false;
    }

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

/// <summary>A dashboard figure that changed after the dashboard was built.</summary>
/// <param name="SectionTitle">Title of the section holding the item.</param>
/// <param name="Item">The item with its new value.</param>
public sealed record DashboardItemChanged(string SectionTitle, DashboardItem Item);
