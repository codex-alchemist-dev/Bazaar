using Avalonia.Controls;

namespace Bazaar.Client.GitGui.Diff;

/// <summary>Which diff implementation draws a diff.</summary>
public enum DiffSurfaceKind
{
    /// <summary>The Avalonia text diff in this module. Works on every head, including Android.</summary>
    Native,

    /// <summary>Monaco's diff editor, supplied by the Athanor module once spike S12 passes (doc 41 4.15).</summary>
    Monaco,
}

/// <summary>The seam between the Git panes and whatever draws a diff (doc 52, Client).</summary>
public interface IDiffSurface
{
    DiffSurfaceKind Kind { get; }

    /// <summary>Builds the control for one file's diff.</summary>
    Control Create(DiffModel model);
}

/// <summary>
/// Picks the diff surface. This module registers only the native one; an Athanor module may add Monaco later. Asking for a
/// kind nobody registered returns the native surface, so a pane never fails for a missing editor.
/// </summary>
public sealed class DiffSurfaceRegistry
{
    private readonly Dictionary<DiffSurfaceKind, IDiffSurface> _surfaces = new()
    {
        [DiffSurfaceKind.Native] = new NativeDiffSurface(),
    };

    /// <summary>Adds or replaces the surface for its kind. The native surface can be replaced by a head that wants its own.</summary>
    public void Register(IDiffSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surfaces[surface.Kind] = surface;
    }

    /// <summary>The surface for <paramref name="kind"/>, or the native one when none is registered.</summary>
    public IDiffSurface Resolve(DiffSurfaceKind kind = DiffSurfaceKind.Native) =>
        _surfaces.TryGetValue(kind, out var surface) ? surface : _surfaces[DiffSurfaceKind.Native];
}
