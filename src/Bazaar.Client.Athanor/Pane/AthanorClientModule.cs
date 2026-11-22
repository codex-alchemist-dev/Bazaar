using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>The Athanor client module: its manifest and what it registers (doc 57 §2, §8; doc 41 §8).</summary>
public sealed class AthanorClientModule : IClientModule
{
    // The validator requires a dotted id, so the module is bazaar.athanor, like bazaar.gc in its own examples.
    public const string ModuleId = "bazaar.athanor";

    public const string OpenCommandId = "athanor.open";
    public const string NewCommandId = "athanor.new";
    public const string SaveCommandId = "athanor.save";
    public const string DiffCommandId = "athanor.diff";

    /// <summary>The commands with their default mnemonics, all under the code <c>ed</c>. None is a Ctrl chord (doc 57 §8).</summary>
    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(OpenCommandId, "Editor: open document", "edopen"),
        new(NewCommandId, "Editor: new document", "ednew"),
        new(SaveCommandId, "Editor: save document", "edsave"),
        new(DiffCommandId, "Editor: diff against server copy", "eddiff"),
    ];

    /// <summary>The command ids, in manifest order.</summary>
    public static IReadOnlyList<string> CommandIds { get; } = [.. Commands.Select(c => c.Id)];

    public ModuleManifest Manifest { get; } = new(
        Id: ModuleId,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: [AthanorPaneKinds.Editor],
        Commands: [.. CommandIds],
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

        foreach (var command in Commands)
        {
            builder.AddCommand(command);
        }
    }
}
