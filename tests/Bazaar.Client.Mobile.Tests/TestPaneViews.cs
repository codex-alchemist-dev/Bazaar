using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Mobile.Tests;

/// <summary>
/// The view factory the Mobile tests hand to <see cref="PaneRegistry"/>. The Mobile module does not reference Client.Core,
/// so the tests use this small stand-in for the fallback views instead of the Core one.
/// </summary>
internal sealed class TestPaneViews : IPaneViewFactory
{
    public static TestPaneViews Instance { get; } = new();

    private TestPaneViews()
    {
    }

    public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Kind };

    public Control WithTitle(string title, Control body) => new StackPanel
    {
        Children =
        {
            new TextBlock { Text = title },
            body,
        },
    };
}
