using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Engines;

/// <summary>
/// Registers the view for engine panes. Engine pane kinds are namespaced by the engine (for example <c>godot.scene-tree</c>)
/// and are not known at compile time, so this module registers no kind of its own: it only ships the view for
/// <see cref="EnginePaneViewModel"/>, which the registry builds for any engine pane view model a head creates.
/// </summary>
public sealed class EnginePaneModule : IPaneModule, IClientModule
{
    /// <summary>The module's identifier. It names no pane kind, because the module registers none.</summary>
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(EnginePaneModule), "bazaar.engines", [], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => "engine";

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.RegisterView<EnginePaneViewModel>(pane => new EnginePaneView(pane));
    }
}
