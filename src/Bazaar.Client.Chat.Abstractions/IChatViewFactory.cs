using Avalonia.Controls;

namespace Bazaar.Client.Chat.Abstractions;

/// <summary>
/// Builds the chat view for a chat. A feature module that shows a chat (the GC pane) takes this from the head, so it
/// can place the view without referencing the chat implementation.
/// </summary>
public interface IChatViewFactory
{
    /// <summary>Returns a new view bound to <paramref name="chat"/>.</summary>
    Control Create(IChatViewModel chat);
}
