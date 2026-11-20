using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.Android;

/// <summary>Identity, host requirements and capability report of the Android emulator provider (doc 62 section 5.1).</summary>
public static class AndroidModule
{
    /// <summary>Kind name listed in <c>vms.devices.enabled</c>.</summary>
    public const string Kind = "android";

    /// <summary>
    /// Windows or Linux x64 with the Android emulator and its hypervisor acceleration present. The macOS host belongs to the parked Apple work (doc 62).
    /// </summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["windows", "linux"],
        ["x64"],
        ["android-emulator"]);

    /// <summary>
    /// Egress is NOT enforced (doc 62 section 8, rule 2): emulator proxy flags cover HTTP only. Until a spike proves otherwise the
    /// lifecycle must keep credentials away from this guest.
    /// </summary>
    public static DeviceProviderCapabilities Capabilities { get; } = new(
        GuestOs: "android",
        Isolation: DeviceIsolation.HardwareVirtualised,
        Save: true,
        Checkpoint: true,
        Restore: true,
        Headless: true,
        MaxConcurrentInstances: 2,
        GuestAgent: false,
        EgressEnforced: false,
        ParentContentPinned: true,
        ScreenSource: true,
        RemoteNode: false);
}
