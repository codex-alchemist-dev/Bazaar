using Avalonia.Controls;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// An <see cref="IEditorHost"/> whose editing surface is an Avalonia control the pane places in its layout (doc 57 §2.2).
/// A host that is itself a <see cref="Control"/> needs no member; this is for hosts that own a root control.
/// </summary>
public interface IEditorSurfaceHost
{
    /// <summary>The control to place in the pane. Owned by the host.</summary>
    Control Surface { get; }
}
