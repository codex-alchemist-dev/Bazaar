using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.MacNode;

namespace Bazaar.Server.Vms.MacOs;

/// <summary>
/// Identity, host requirements and capability report of the macOS VM provider (doc 62 section 5.3).
/// <para>
/// Apple rule: macOS guests run only on Apple-branded hardware, on Apple silicon here, with at most two virtual instances per Mac.
/// This module never runs one on the primary server; it drives Apple's Virtualization framework on a Mac node through
/// <see cref="IMacNodeLink"/>. There is no emulation path and none will be built.
/// </para>
/// </summary>
public static class MacOsModule
{
    /// <summary>Kind name listed in <c>vms.devices.enabled</c>.</summary>
    public const string Kind = "macos";

    /// <summary>Checked against the Mac node's facts (doc 62 sections 3 and 6): macOS on Apple silicon with the virtualization framework.</summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["macos"],
        ["arm64"],
        [MacNodeRules.VirtualizationFeature]);

    /// <summary>
    /// Save, checkpoint and restore are false until spike S-MAC passes, and so is the screen source (doc 62 section 5.3). Concurrency is
    /// capped at Apple's two. Egress is NOT enforced (section 8, rule 2). The guest agent is baked into the golden image.
    /// </summary>
    public static DeviceProviderCapabilities Capabilities { get; } = new(
        GuestOs: "macos",
        Isolation: DeviceIsolation.HardwareVirtualised,
        Save: false,
        Checkpoint: false,
        Restore: false,
        Headless: true,
        MaxConcurrentInstances: MacOsInstanceCap.HardCap,
        GuestAgent: true,
        EgressEnforced: false,
        ParentContentPinned: true,
        ScreenSource: false,
        RemoteNode: true);
}
