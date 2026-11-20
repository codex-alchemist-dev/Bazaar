using System.Reflection;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Panes.Abstractions;

/// <summary>
/// Builds the <see cref="ModuleManifest"/> of a first-party client module (doc 41 section 8). First-party modules are
/// compiled into the head, so isolation is fixed, the contract version is the current one and the host requirements
/// name every OS and architecture the client ships on. The version comes from the module's own assembly, so a company
/// build and the host read the same value.
/// </summary>
public static class FirstPartyManifest
{
    /// <summary>The client module contract version these manifests declare.</summary>
    public const int ContractVersion = 1;

    /// <summary>The fallback version when an assembly carries none.</summary>
    private const string _fallbackVersion = "0.1.0";

    /// <summary>Creates the manifest for a module whose code lives in <paramref name="moduleType"/>'s assembly.</summary>
    /// <param name="moduleType">Any type of the module's assembly; its version becomes the manifest version.</param>
    /// <param name="id">Dotted module id. First-party modules use the pane kind they register.</param>
    /// <param name="paneKinds">The pane kinds the module registers. Empty for a module that only adds views.</param>
    /// <param name="capabilities">The capability classes the module needs, from <see cref="ModuleManifestValidator.KnownCapabilities"/>.</param>
    /// <param name="commands">Command ids the module contributes. Null for none.</param>
    /// <param name="settingKeys">Setting keys the module reads. Null for none.</param>
    /// <param name="dependsOn">Ids of modules this one needs. Null for none.</param>
    /// <param name="softDependsOn">Ids of modules this one uses when present. Null for none.</param>
    public static ModuleManifest Create(
        Type moduleType,
        string id,
        IReadOnlyList<string> paneKinds,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string>? commands = null,
        IReadOnlyList<string>? settingKeys = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? softDependsOn = null)
    {
        ArgumentNullException.ThrowIfNull(moduleType);

        return new ModuleManifest(
            Id: id,
            Version: VersionOf(moduleType.Assembly),
            ContractVersion: ContractVersion,
            PaneKinds: paneKinds,
            Commands: commands ?? [],
            SettingKeys: settingKeys ?? [],
            Capabilities: capabilities,
            Isolation: ModuleIsolation.InProcessCompiled,
            Host: new HostRequirements(
                HostRequirements.CurrentSchemaVersion,
                HostCompatibility.KnownOperatingSystems.Order(StringComparer.Ordinal).ToArray(),
                HostCompatibility.KnownArchitectures.Order(StringComparer.Ordinal).ToArray(),
                []),
            DependsOn: dependsOn,
            SoftDependsOn: softDependsOn);
    }

    private static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? _fallbackVersion : informational;
    }
}
