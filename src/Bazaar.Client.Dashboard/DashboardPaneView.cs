using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Dashboard;

/// <summary>
/// The dashboard's body: each section's title, then its items. A figure that is a whole number shows as a count badge that pops in
/// and bumps as the count rises (doc 93, "Unread badge and counts"); any other value shows as "label: value".
/// </summary>
public static class DashboardPaneView
{
    public static StackPanel Create(DashboardPaneViewModel dashboard)
    {
        var sections = new StackPanel();
        ThemeBinding.BindLength(sections, "space.md", gap => sections.Spacing = gap);
        var badges = new Dictionary<(string Section, string Label), CountBadge>();
        foreach (var section in dashboard.Sections)
        {
            sections.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeight.SemiBold });
            foreach (var item in section.Items)
            {
                if (item.Count is { } count)
                {
                    var badge = new CountBadge { Count = count, Name = BadgeName(section.Title, item.Label) };
                    badges[(section.Title, item.Label)] = badge;
                    sections.Children.Add(CountRow(item.Label, badge));
                }
                else
                {
                    sections.Children.Add(new TextBlock { Text = $"{item.Label}: {item.Value}" });
                }
            }
        }

        // Follows the view model while the panel is in a window, so a closed pane never stays subscribed.
        void OnChanged(object? sender, DashboardItemChanged e)
        {
            if (badges.TryGetValue((e.SectionTitle, e.Item.Label), out var badge) && e.Item.Count is { } count) badge.Count = count;
        }

        sections.AttachedToVisualTree += (_, _) => dashboard.ItemChanged += OnChanged;
        sections.DetachedFromVisualTree += (_, _) => dashboard.ItemChanged -= OnChanged;
        return sections;
    }

    /// <summary>The name of the badge control for an item, so tests and automation can find it.</summary>
    public static string BadgeName(string sectionTitle, string label) => $"Badge.{sectionTitle}.{label}";

    private static StackPanel CountRow(string label, CountBadge badge)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        ThemeBinding.BindLength(row, "space.sm", gap => row.Spacing = gap);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(badge);
        return row;
    }
}
