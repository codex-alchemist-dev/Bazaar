using Bazaar.Contracts.Modules;

namespace Bazaar.SupportBundle;

/// <summary>
/// The client module that puts "Save support bundle" in the command palette (doc 37 section 4d). It only contributes the
/// command; the head decides where the zip goes and runs <see cref="SaveCommandId"/> when the palette reports it. The bundle
/// is written locally and never sent anywhere.
/// </summary>
public sealed class SupportBundleClientModule : IClientModule
{
    /// <summary>The module id in the client manifest (dotted, as the manifest validator requires).</summary>
    public const string ModuleId = "bazaar.supportbundle";

    /// <summary>The palette command that saves a support bundle.</summary>
    public const string SaveCommandId = "supportbundle.save";

    /// <summary>The manifest. Versioned with the repo's VersionPrefix.</summary>
    public static ModuleManifest DefaultManifest { get; } = new(
        Id: ModuleId,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: [],
        Commands: [SaveCommandId],
        SettingKeys: [],
        Capabilities: ["storage:local"],
        Isolation: ModuleIsolation.InProcessCompiled,
        Host: new HostRequirements(
            HostRequirements.CurrentSchemaVersion,
            ["windows", "linux"],
            ["x64", "arm64"],
            []));

    /// <summary>The command with its default mnemonic.</summary>
    public static ClientCommandContribution SaveCommand { get; } = new(SaveCommandId, "Save support bundle", "bundle");

    /// <inheritdoc />
    public ModuleManifest Manifest => DefaultManifest;

    /// <inheritdoc />
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddCommand(SaveCommand);
    }
}
