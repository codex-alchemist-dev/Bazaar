using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.MacNode;

namespace Bazaar.Server.Vms.Ios;

/// <summary>
/// Identity, host requirements and capability report of the iOS Simulator provider (doc 62 section 5.2).
/// <para>
/// Apple rule: the iOS Simulator runs only on Apple hardware. This module never runs it on the primary server; it drives it on a Mac
/// node (<see cref="IMacNodeLink"/>). The simulator is not a virtual machine: it is a set of macOS processes that share the Mac's
/// kernel, file system and user account, which is why the report says <see cref="DeviceIsolation.HostProcess"/>.
/// </para>
/// </summary>
public static class IosModule
{
    /// <summary>Kind name listed in <c>vms.devices.enabled</c>.</summary>
    public const string Kind = "ios";

    /// <summary>
    /// The requirements are checked against the Mac node's facts, not the primary server's (doc 62 sections 3 and 6): macOS, Xcode and
    /// its simulator tools. Apple silicon or Intel.
    /// </summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["macos"],
        ["arm64", "x64"],
        [MacNodeRules.XcodeSimulatorFeature]);

    /// <summary>
    /// Save, checkpoint and restore are false: <c>simctl</c> has no supported call for them (doc 62 section 5.2). Egress is NOT enforced
    /// (section 8, rule 2), so the lifecycle keeps credentials away from this guest. There is no guest agent: nothing runs inside the
    /// simulated OS.
    /// </summary>
    public static DeviceProviderCapabilities Capabilities { get; } = new(
        GuestOs: "ios",
        Isolation: DeviceIsolation.HostProcess,
        Save: false,
        Checkpoint: false,
        Restore: false,
        Headless: true,
        MaxConcurrentInstances: 2,
        GuestAgent: false,
        EgressEnforced: false,
        ParentContentPinned: true,
        ScreenSource: true,
        RemoteNode: true);
}
