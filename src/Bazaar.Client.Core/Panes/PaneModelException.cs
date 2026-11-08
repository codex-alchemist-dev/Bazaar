namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Thrown by a pane factory when the descriptor's model does not match the kind's shape.
/// <see cref="PaneRegistry"/> turns it into the generic fallback instead of failing the screen.
/// </summary>
public sealed class PaneModelException : Exception
{
    public PaneModelException(string message)
        : base(message)
    {
    }
}
