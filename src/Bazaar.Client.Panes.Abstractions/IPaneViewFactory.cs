using Avalonia.Controls;
using Bazaar.Client.Abstractions.Panes;

namespace Bazaar.Client.Panes.Abstractions;

/// <summary>
/// Builds the Avalonia views a pane registry hands out: the generic fallback and the title chrome around a module's view.
/// The composition root in Client.Core supplies the implementation, so this project names no Client.Core type.
/// </summary>
public interface IPaneViewFactory
{
    /// <summary>Builds the fallback view for <paramref name="pane"/>: the generic tree for a generic pane, a kind label otherwise.</summary>
    Control Create(IPaneViewModel pane);

    /// <summary>Puts <paramref name="title"/> above <paramref name="body"/>.</summary>
    Control WithTitle(string title, Control body);
}
