using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Devices;

/// <summary>
/// Registers the Devices pane (doc 62 section 9): the enabled device providers and the running device guests, list only.
/// The head supplies the <see cref="IDeviceListSource"/> through <see cref="PaneModuleContext.With{T}"/>; with none the
/// pane has nothing to show and hides itself. The Android head ships it as a development console, list only.
/// </summary>
public sealed class DevicesPaneModule : IPaneModule, IClientModule
{
    /// <summary>The pane kind this module registers.</summary>
    public const string PaneKind = "bazaar.devices";

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(DevicesPaneModule), PaneKind, [PaneKind], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var source = context.Get<IDeviceListSource>();
        registry.Register(PaneKind, descriptor => new DevicesPaneViewModel(descriptor, source));
        registry.RegisterView<DevicesPaneViewModel>(pane => new DevicesPaneView(pane));
    }
}
