using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The views Client.Core owns: the generic fallback and the bodies of its own panes. A feature module registers its
/// view with <see cref="PaneRegistry.RegisterView{TPane}"/> instead of adding a case here. The registry reaches this
/// class through <see cref="IPaneViewFactory"/>; <see cref="Instance"/> is the one the composition root passes in.
/// </summary>
public sealed class PaneViews : IPaneViewFactory
{
    /// <summary>The view factory the composition root hands to <see cref="PaneRegistry"/>.</summary>
    public static PaneViews Instance { get; } = new();

    private PaneViews()
    {
    }

    public static Control Create(IPaneViewModel pane) => pane switch
    {
        GenericPaneViewModel generic => WithTitle(generic.Title, Generic(generic)),
        _ => WithTitle(pane.Title, new TextBlock { Text = pane.Kind }),
    };

    Control IPaneViewFactory.Create(IPaneViewModel pane) => Create(pane);

    Control IPaneViewFactory.WithTitle(string title, Control body) => WithTitle(title, body);

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
