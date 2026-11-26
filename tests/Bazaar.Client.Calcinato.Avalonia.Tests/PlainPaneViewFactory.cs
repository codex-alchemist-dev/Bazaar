using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Calcinato.Avalonia.Tests;

/// <summary>A view factory with plain chrome, so a test can see which view the registry chose.</summary>
internal sealed class PlainPaneViewFactory : IPaneViewFactory
{
    public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Kind };

    public Control WithTitle(string title, Control body) => new StackPanel { Children = { new TextBlock { Text = title }, body } };
}
