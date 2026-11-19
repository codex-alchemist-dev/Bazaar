using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GC;

/// <summary>
/// The GC pane (pane id <c>bazaar.gc</c>). Wraps the existing group chat view. The chat comes from a live session when
/// the head has one; with none, the pane shows its empty state and makes no server calls.
/// </summary>
public sealed class GcPaneViewModel : IPaneViewModel
{
    /// <summary>Pane id of the GC pane (<see cref="Bazaar.Contracts.Panes.PaneKinds.Gc"/>).</summary>
    public const string PaneKind = Bazaar.Contracts.Panes.PaneKinds.Gc;

    /// <summary>Heading used when the descriptor carries no title.</summary>
    public const string DefaultTitle = "GCs";

    /// <param name="descriptor">The pane descriptor. Its model is not read: the chat comes from the head, not the server.</param>
    /// <param name="chat">The group chat to show, or null when no session is connected.</param>
    public GcPaneViewModel(PaneDescriptor descriptor, ChatViewModel? chat = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = descriptor.Kind;
        Title = string.IsNullOrWhiteSpace(descriptor.Title) ? DefaultTitle : descriptor.Title;
        Chat = chat;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The group chat view model, or null when the pane shows its empty state.</summary>
    public ChatViewModel? Chat { get; }
}
