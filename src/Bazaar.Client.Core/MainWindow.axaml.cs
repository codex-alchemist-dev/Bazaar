using Avalonia.Controls;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Replaces the start content with a scrollable stack of panes. Panes are built through the registry, so
    /// unknown kinds render generically. Until a pane source is wired, nothing calls this.
    /// </summary>
    public void ShowPanes(IEnumerable<PaneDescriptor> panes, PaneRegistry registry) =>
        Content = new ScrollViewer { Content = PaneHost.Build(panes, registry) };
}
