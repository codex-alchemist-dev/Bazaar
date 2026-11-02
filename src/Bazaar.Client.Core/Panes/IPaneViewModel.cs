namespace Bazaar.Client.Core.Panes;

/// <summary>
/// What a pane view needs from its view model. Views are chosen by type in <see cref="PaneViews"/>,
/// so a view model carries data only and never references Avalonia controls.
/// </summary>
public interface IPaneViewModel
{
    /// <summary>The pane kind the view model was built from. The generic fallback keeps the server's kind.</summary>
    string Kind { get; }

    /// <summary>Heading shown on the pane.</summary>
    string Title { get; }
}
