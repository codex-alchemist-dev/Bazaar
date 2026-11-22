using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Windowing;

/// <summary>
/// The "bazaar.client.windowing" client module (doc 41 section 8): detached panes, standalone editor and terminal windows,
/// and the launch router. Compiled into the head, so it is <see cref="ModuleIsolation.InProcessCompiled"/>.
/// </summary>
public sealed class WindowingModule : IClientModule
{
    /// <summary>Module id, as the manifest and the host know it.</summary>
    public const string ModuleId = "bazaar.client.windowing";

    /// <summary>The manifest this module declares. Checked by <see cref="ModuleManifestValidator"/> before the head registers the module.</summary>
    public static ModuleManifest DeclaredManifest { get; } = new(
        Id: ModuleId,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: [],
        Commands: ["window.detach", "window.dock", "window.new-editor", "window.new-terminal"],
        SettingKeys: ["windows.startup.budget_detached_window_ms"],
        Capabilities: ["ui:pane"],
        Isolation: ModuleIsolation.InProcessCompiled,
        Host: new HostRequirements(
            HostRequirements.CurrentSchemaVersion,
            ["windows", "linux"],
            ["x64", "arm64"],
            []));

    /// <inheritdoc/>
    public ModuleManifest Manifest => DeclaredManifest;

    /// <summary>Records the module's four commands with the head.</summary>
    /// <param name="builder">The registration surface the head created for this module.</param>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddCommand("window.detach");
        builder.AddCommand("window.dock");
        builder.AddCommand("window.new-editor");
        builder.AddCommand("window.new-terminal");
    }
}
