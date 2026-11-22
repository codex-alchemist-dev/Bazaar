using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Calcinato.Tests;

/// <summary>A view factory for tests that only read view models: the registry never asks it for a view.</summary>
internal sealed class UnusedPaneViewFactory : IPaneViewFactory
{
    public Control Create(IPaneViewModel pane) => throw new NotSupportedException("These tests read view models only.");

    public Control WithTitle(string title, Control body) => throw new NotSupportedException("These tests read view models only.");
}
