using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.MicroVm;

/// <summary>The microVM monitors the provider can drive (doc 62 section 5.0). <c>vms.microvm.monitor</c>.</summary>
public enum MicroVmMonitorKind
{
    Firecracker,
    CloudHypervisor,
}

/// <summary>Identity, host requirements and capability report of the microVM provider (doc 62 section 5.0).</summary>
public static class MicroVmModule
{
    /// <summary>Kind name listed in <c>vms.devices.enabled</c>.</summary>
    public const string Kind = "microvm";

    /// <summary>Linux with KVM and one of the monitors installed.</summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["linux"],
        ["x64", "arm64"],
        ["kvm", "microvm"]);

    /// <summary>
    /// Save, checkpoint and restore are reported false until spike S-MVM confirms monitor snapshots on the target kernel (doc 62 section 11),
    /// and egress is reported false until a tap in its own network namespace is proven mandatory. The real monitor adapters
    /// (<c>Monitors/</c>) implement boot, graceful stop and kill only; they do not use the snapshot APIs, so these stay false.
    /// </summary>
    public static MicroVmCapabilities Capabilities { get; } = new(
        Save: false,
        Checkpoint: false,
        Restore: false,
        Headless: true,
        LinuxGuestsOnly: true,
        GuestAgent: true,
        EgressEnforced: false,
        ParentContentPinned: true);
}

/// <summary>What the microVM provider supports. Every value is backed by <see cref="MicroVmBackend"/> behaviour.</summary>
public sealed record MicroVmCapabilities(
    bool Save,
    bool Checkpoint,
    bool Restore,
    bool Headless,
    bool LinuxGuestsOnly,
    bool GuestAgent,
    bool EgressEnforced,
    bool ParentContentPinned);
