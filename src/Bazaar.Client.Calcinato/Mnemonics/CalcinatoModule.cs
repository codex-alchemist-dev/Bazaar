using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Calcinato.Mnemonics;

/// <summary>
/// The Calcinato client module (doc 55, section 7). Shipped, so it is compiled into the head. Its one permission class is a
/// local process (the PTY). Remote attach needs the network host class, which has no client capability yet, so the
/// manifest does not carry it (doc 48, section 1).
/// </summary>
public sealed class CalcinatoModule : IClientModule
{
    /// <summary>The manifest. Versioned with the repo's VersionPrefix.</summary>
    public static ModuleManifest DefaultManifest { get; } = new(
        Id: CalcinatoCommands.ModuleId,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: [],
        Commands: CalcinatoCommands.CommandIds,
        SettingKeys: [],
        Capabilities: ["process:spawn"],
        Isolation: ModuleIsolation.InProcessCompiled,
        Host: new HostRequirements(
            HostRequirements.CurrentSchemaVersion,
            ["windows", "linux"],
            ["x64", "arm64"],
            []));

    public ModuleManifest Manifest => DefaultManifest;

    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (string command in CalcinatoCommands.CommandIds)
        {
            builder.AddCommand(command);
        }
    }
}
