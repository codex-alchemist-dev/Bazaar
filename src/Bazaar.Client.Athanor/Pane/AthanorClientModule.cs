using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>The Athanor client module: its manifest and what it registers (doc 57 §2, §8; doc 41 §8).</summary>
public sealed class AthanorClientModule : IClientModule
{
    // The validator requires a dotted id, so the module is bazaar.athanor, like bazaar.gc in its own examples.
    public const string ModuleId = "bazaar.athanor";

    public ModuleManifest Manifest { get; } = new(
        Id: ModuleId,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: [AthanorPaneKinds.Editor],
        Commands: ["athanor.open", "athanor.new", "athanor.save", "athanor.diff"],
        SettingKeys: [],
        Capabilities: ["wire:vault.read", "storage:local", "ui:pane"],
        Isolation: ModuleIsolation.InProcessCompiled,
        Host: new HostRequirements(HostRequirements.CurrentSchemaVersion, ["windows", "linux"], ["x64"], []));

    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var kind in Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }

        foreach (var command in Manifest.Commands)
        {
            builder.AddCommand(command);
        }
    }
}
