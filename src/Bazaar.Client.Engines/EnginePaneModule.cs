using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Engines;

/// <summary>
/// Registers the view for engine panes. Engine pane kinds are namespaced by the engine (for example <c>godot.scene-tree</c>)
/// and are not known at compile time, so this module registers no kind of its own: it only ships the view for
/// <see cref="EnginePaneViewModel"/>, which the registry builds for any engine pane view model a head creates.
/// </summary>
public sealed class EnginePaneModule : IPaneModule
{
    /// <summary>The module's identifier. It names no pane kind, because the module registers none.</summary>
    public string Id => "engine";

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.RegisterView<EnginePaneViewModel>(pane => new EnginePaneView(pane));
    }
}
