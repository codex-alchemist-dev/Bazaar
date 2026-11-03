using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Terminal;

/// <summary>
/// Plain-text lines for a pane, for the terminal head. Built from the same view models as the Avalonia views,
/// so both heads show the same data. No console code, so it can be unit tested.
/// </summary>
public static class PaneTextFormatter
{
    public static IReadOnlyList<string> Format(PaneDescriptor descriptor, PaneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(registry);

        var pane = registry.Create(descriptor);
        var lines = new List<string> { pane.Title };

        switch (pane)
        {
            case KanbanPaneViewModel kanban:
                foreach (var column in kanban.Columns)
                {
                    lines.Add($"  {column.Name} ({column.Cards.Count})");
                    foreach (var card in column.Cards)
                    {
                        lines.Add(card.IsBlocked ? $"    - {card.Title} [Blocked]" : $"    - {card.Title}");
                    }
                }

                break;
            case DashboardPaneViewModel dashboard:
                foreach (var section in dashboard.Sections)
                {
                    lines.Add($"  {section.Title}");
                    foreach (var item in section.Items)
                    {
                        lines.Add($"    {item.Label}: {item.Value}");
                    }
                }

                break;
            case GenericPaneViewModel generic:
                lines.AddRange(generic.Lines.Select(line => "  " + line));
                break;
        }

        return lines;
    }
}
