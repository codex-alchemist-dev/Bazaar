using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Settings;

/// <summary>
/// Registers the settings pane (<see cref="PaneKinds.Settings"/>) and the <c>nav.settings</c> palette command. A head hands
/// its <see cref="ISettingsClient"/> through <see cref="PaneModuleContext.With{T}"/>; without one the pane says it is not connected.
/// </summary>
public sealed class SettingsPaneModule : IPaneModule, IClientModule
{
    /// <summary>Command id that opens the settings pane; the same id the default navigation vocabulary carries.</summary>
    public const string OpenCommandId = "nav.settings";

    /// <summary>The palette commands this module contributes.</summary>
    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(OpenCommandId, "Settings", "settings"),
    ];

    /// <inheritdoc />
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(
        typeof(SettingsPaneModule),
        PaneKinds.Settings,
        [PaneKinds.Settings],
        ["ui:pane"],
        Commands.Select(c => c.Id).ToArray());

    /// <inheritdoc />
    public string Id => PaneKinds.Settings;

    /// <inheritdoc />
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }

        foreach (var command in Commands)
        {
            builder.AddCommand(command);
        }
    }

    /// <inheritdoc />
    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var client = context.Get<ISettingsClient>() ?? NotConnectedSettingsClient.Instance;
        registry.Register(PaneKinds.Settings, descriptor => new SettingsPaneViewModel(descriptor, client));
        registry.RegisterView<SettingsPaneViewModel>(SettingsPaneView.Create);
    }
}
