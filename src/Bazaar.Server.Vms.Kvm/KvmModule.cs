using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.Kvm;

/// <summary>
/// Identity and host requirements of the KVM VM provider (doc 16, "KVM control from Linux"). The requirements are typed metadata:
/// the provider checks them at selection, and the install path checks the same declaration.
/// </summary>
public static class KvmModule
{
    /// <summary>The kind name the <c>host.provider.vm</c> setting selects.</summary>
    public const string Kind = "kvm";

    /// <summary>KVM needs x64 Linux with <c>/dev/kvm</c> present.</summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["linux"],
        ["x64"],
        ["kvm"]);

    /// <summary>
    /// What this provider can and cannot do. <see cref="KvmCapabilities.WindowsGuests"/> is false: a Windows guest on a Linux host has
    /// no guest transport yet (doc 16, Question 1, OPEN), so the backend refuses a Windows spec.
    /// </summary>
    public static KvmCapabilities Capabilities { get; } = new(
        Save: true,
        Checkpoint: true,
        Restore: true,
        LinuxGuests: true,
        WindowsGuests: false,
        Ephemeral: false,
        MemoryConfigurable: true,
        DiskCapEnforced: false,
        ParentContentPinned: false);
}

/// <summary>
/// What the KVM provider supports. <see cref="DiskCapEnforced"/> false means <c>vm.*.disk_cap_gb</c> cannot be honoured: a qcow2
/// child takes the virtual size of its backing file. <see cref="ParentContentPinned"/> false means the golden image verifier owns the rule.
/// </summary>
public sealed record KvmCapabilities(
    bool Save,
    bool Checkpoint,
    bool Restore,
    bool LinuxGuests,
    bool WindowsGuests,
    bool Ephemeral,
    bool MemoryConfigurable,
    bool DiskCapEnforced,
    bool ParentContentPinned);
