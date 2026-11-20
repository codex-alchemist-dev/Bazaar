using Avalonia.Controls;
using Bazaar.Client.Chat.Abstractions;

namespace Bazaar.Client.Chat;

/// <summary>Builds the group chat view (<see cref="ChatView"/>) for a chat.</summary>
public sealed class ChatViewFactory : IChatViewFactory
{
    /// <summary>The factory the heads pass to the GC pane and the main window.</summary>
    public static ChatViewFactory Instance { get; } = new();

    public Control Create(IChatViewModel chat) => new ChatView { DataContext = chat };
}
