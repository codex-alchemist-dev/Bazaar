namespace Bazaar.Client.Panes.Abstractions;

/// <summary>
/// Brings a pane into view. Each head implements it for its own layout (the phone pager selects the page that holds the
/// pane; a desktop window shows the pane). Pane modules ask for a pane by kind and never touch the layout.
/// </summary>
public interface IPaneNavigator
{
    /// <summary>Shows the pane of <paramref name="paneKind"/>. Returns false when the layout cannot show it.</summary>
    bool Show(string paneKind);
}
