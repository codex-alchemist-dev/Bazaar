using Avalonia.Controls;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core;

public sealed partial class MainWindow : Window
{
    public MainWindow() : this(null)
    {
    }

    /// <param name="chat">The group chat to show. Null shows the plain start window.</param>
    public MainWindow(ChatViewModel? chat)
    {
        InitializeComponent();
        if (chat is not null)
        {
            Content = new ChatView { DataContext = chat };
        }
    }

    /// <summary>
    /// Replaces the start content with a scrollable stack of panes. Panes are built through the registry, so
    /// unknown kinds render generically. Until a pane source is wired, nothing calls this.
    /// </summary>
    public void ShowPanes(IEnumerable<PaneDescriptor> panes, PaneRegistry registry) =>
        Content = new ScrollViewer { Content = PaneHost.Build(panes, registry) };
}
