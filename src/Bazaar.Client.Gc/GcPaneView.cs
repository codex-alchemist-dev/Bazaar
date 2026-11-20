using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.Chat.Abstractions;

namespace Bazaar.Client.Gc;

/// <summary>Builds the GC pane's body. The chat view comes from the head's <see cref="IChatViewFactory"/>.</summary>
internal static class GcPaneView
{
    internal static Control Create(GcPaneViewModel gc, IChatViewFactory? chats)
    {
        if (gc.Chat is null || chats is null)
        {
            return new TextBlock { Text = "No group chat is connected.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        }

        // A fixed height gives the chat's message list a bounded area inside the stacked pane.
        return new Border { Height = 480, Child = chats.Create(gc.Chat) };
    }
}
