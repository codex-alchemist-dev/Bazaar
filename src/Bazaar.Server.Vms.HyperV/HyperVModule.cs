using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.HyperV;

/// <summary>
/// Identity and host requirements of the Hyper-V VM provider (doc 16). The requirements are typed metadata: the provider
/// checks them at selection, and the install path checks the same declaration (owner, 2026-11-16).
/// </summary>
public static class HyperVModule
{
    /// <summary>The kind name the <c>host.provider.vm</c> setting selects.</summary>
    public const string Kind = "hyperv";

    /// <summary>Hyper-V needs x64 Windows with the Hyper-V role enabled.</summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["windows"],
        ["x64"],
        ["hyperv"]);

    /// <summary>
    /// What this provider can and cannot do, as the status page and the lifecycle service read it. Each value is backed by
    /// <see cref="HyperVBackend"/>.
    /// </summary>
    public static HyperVCapabilities Capabilities { get; } = new(
        Save: true,
        Checkpoint: true,
        Restore: true,
        LinuxGuests: true,
        Ephemeral: false,
        MemoryConfigurable: true,
        DiskCapEnforced: false,
        ParentContentPinned: false);
}

/// <summary>
/// What the Hyper-V provider supports. <see cref="DiskCapEnforced"/> false means <c>vm.*.disk_cap_gb</c> cannot be honoured: a
/// differencing disk takes the virtual size of its parent. <see cref="ParentContentPinned"/> false means the provider does not
/// itself stop a parent from changing after children exist; the golden image verifier (hash and ACL check) owns that rule.
/// </summary>
public sealed record HyperVCapabilities(
    bool Save,
    bool Checkpoint,
    bool Restore,
    bool LinuxGuests,
    bool Ephemeral,
    bool MemoryConfigurable,
    bool DiskCapEnforced,
    bool ParentContentPinned);
