using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Dashboard;

/// <summary>The dashboard's body: each section's title, then its items as "label: value" lines.</summary>
public static class DashboardPaneView
{
    public static StackPanel Create(DashboardPaneViewModel dashboard)
    {
        var sections = new StackPanel { Spacing = 12 };
        foreach (var section in dashboard.Sections)
        {
            sections.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeight.SemiBold });
            foreach (var item in section.Items)
            {
                sections.Children.Add(new TextBlock { Text = $"{item.Label}: {item.Value}" });
            }
        }

        return sections;
    }
}
