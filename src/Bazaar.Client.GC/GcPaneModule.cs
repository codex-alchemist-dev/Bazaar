using Avalonia.Controls;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.GC;

/// <summary>
/// Registers the group chat pane (<see cref="GcPaneViewModel.PaneKind"/>). The chat comes from the head through
/// <see cref="GcPanes.WithChat"/>; with none, the pane shows its empty state.
/// </summary>
public sealed class GcPaneModule : IPaneModule
{
    public string Id => GcPaneViewModel.PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var chat = context.Get<ChatViewModel>();
        registry.Register(GcPaneViewModel.PaneKind, descriptor => new GcPaneViewModel(descriptor, chat));
        registry.RegisterView<GcPaneViewModel>(View);
    }

    private static Control View(GcPaneViewModel gc)
    {
        if (gc.Chat is null)
        {
            return new TextBlock { Text = "No group chat is connected.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }

        // A fixed height gives the chat's message list a bounded area inside the stacked pane.
        return new Border { Height = 480, Child = new ChatView { DataContext = gc.Chat } };
    }
}
