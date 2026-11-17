using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The views Client.Core owns: the generic fallback and the bodies of its own panes. A feature module registers its
/// view with <see cref="PaneRegistry.RegisterView{TPane}"/> instead of adding a case here.
/// </summary>
public static class PaneViews
{
    public static Control Create(IPaneViewModel pane) => pane switch
    {
        GenericPaneViewModel generic => WithTitle(generic.Title, Generic(generic)),
        _ => WithTitle(pane.Title, new TextBlock { Text = pane.Kind }),
    };

    internal static StackPanel WithTitle(string title, Control body) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 },
            body,
        },
    };

    internal static Control GcPane(GcPaneViewModel gc)
    {
        if (gc.Chat is null)
        {
            return new TextBlock { Text = "No group chat is connected.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        }

        // A fixed height gives the chat's message list a bounded area inside the stacked pane.
        return new Border { Height = 480, Child = new ChatView { DataContext = gc.Chat } };
    }

    internal static StackPanel Dashboard(DashboardPaneViewModel dashboard)
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

    private static ScrollViewer Generic(GenericPaneViewModel generic)
    {
        var lines = new StackPanel { Spacing = 2 };
        foreach (var line in generic.Lines)
        {
            lines.Children.Add(new TextBlock { Text = line, FontFamily = new FontFamily("monospace") });
        }

        return new ScrollViewer { Content = lines };
    }
}
