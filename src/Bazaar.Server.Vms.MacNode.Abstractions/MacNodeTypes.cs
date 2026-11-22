using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.MacNode;

/// <summary>
/// The Apple hardware rule (doc 62 section 3), stated once for every module that talks to a Mac node.
/// </summary>
/// <remarks>
/// iOS and macOS guests run only on Apple-branded hardware. Apple's licence allows macOS in a virtual machine only on a Mac (with a
/// cap of two virtual instances per Mac), and Xcode and the iOS Simulator are licensed for Apple hardware. A Windows or Linux Bazaar
/// server therefore reaches these guests only through a Mac node: a second, headless Bazaar process on a Mac the operator owns.
/// There is no emulation path and none will be built. Operators are responsible for their own licence compliance; the modules only
/// enforce the technical cap. Counsel reviews the wording before the first public release.
/// </remarks>
public static class MacNodeRules
{
    /// <summary>One plain sentence for the wizard, the status page and refusal reasons.</summary>
    public const string AppleHardwareRule = "iOS and macOS guests run only on Apple hardware, through a Mac node.";

    /// <summary>Apple's cap on macOS virtual machines per Mac (doc 62 section 3). Settings may lower the macOS provider's cap, never raise it.</summary>
    public const int MaxMacOsVmInstances = 2;

    /// <summary>The reason a provider reports while no node is linked (doc 62 section 6).</summary>
    public const string NoNodeLinkedReason = "no Mac node linked";

    /// <summary>Host feature the node reports when Xcode and the simulator tools are present.</summary>
    public const string XcodeSimulatorFeature = "xcode-simulator";

    /// <summary>Host feature the node reports when Virtualization.framework is available (Apple silicon).</summary>
    public const string VirtualizationFeature = "virtualization-framework";
}

/// <summary>Where the link to a Mac node stands. The node connects out to the primary (doc 62 section 6); the primary never dials in.</summary>
public enum MacNodeState
{
    /// <summary>No node is configured or linked. The state of every install that has not set up a Mac.</summary>
    Unlinked,

    /// <summary>A node is configured but not connected now.</summary>
    Disconnected,

    /// <summary>The link is being established.</summary>
    Connecting,

    /// <summary>The node is connected and its identity key matched the pin.</summary>
    Connected,

    /// <summary>The node failed its key check. Refused, never a warning (doc 62 section 8, rule 4).</summary>
    Refused,
}

/// <summary>What a connected Mac node reports about itself. Facts only: no host name, user name or path.</summary>
/// <param name="MacOsVersion">The Mac's macOS version, for example <c>15.1</c>.</param>
/// <param name="Architecture"><c>arm64</c> or <c>x64</c>. macOS guests need <c>arm64</c> (doc 62 section 5.3).</param>
/// <param name="XcodeInstalled">Xcode and its command line tools are present, so the simulator can run.</param>
/// <param name="VirtualizationAvailable">Virtualization.framework is usable, so macOS guests can run.</param>
/// <param name="RunsAsDedicatedUser">The node runs as a dedicated unprivileged macOS user, not the owner's login and not root (doc 62 section 8, rule 3).</param>
/// <param name="MaxVmInstances">The node's own cap on macOS virtual machines. Never above <see cref="MacNodeRules.MaxMacOsVmInstances"/>.</param>
public sealed record MacNodeInfo(
    string MacOsVersion,
    string Architecture,
    bool XcodeInstalled,
    bool VirtualizationAvailable,
    bool RunsAsDedicatedUser,
    int MaxVmInstances)
{
    /// <summary>The node seen as a host for <see cref="HostCompatibility.Check"/>: always <c>macos</c>, plus the features it reports.</summary>
    public HostFacts ToHostFacts()
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        if (XcodeInstalled)
        {
            features.Add(MacNodeRules.XcodeSimulatorFeature);
        }

        if (VirtualizationAvailable)
        {
            features.Add(MacNodeRules.VirtualizationFeature);
        }

        return new HostFacts("macos", Architecture, features);
    }
}

/// <summary>A snapshot of the link: state, a plain reason when it is not connected, and the node's facts when it is.</summary>
public sealed record MacNodeStatus(MacNodeState State, string? Reason, MacNodeInfo? Info)
{
    /// <summary>True only when the node is connected and has reported its facts.</summary>
    public bool IsConnected => State == MacNodeState.Connected && Info is not null;

    /// <summary>The status of an install with no Mac node.</summary>
    public static MacNodeStatus Unlinked { get; } = new(MacNodeState.Unlinked, MacNodeRules.NoNodeLinkedReason, null);

    /// <summary>A plain reason a provider can show when the node cannot serve it, or null when it is connected. Names no address, key or path.</summary>
    public string? UnavailableReason => State switch
    {
        MacNodeState.Connected when Info is not null => null,
        MacNodeState.Unlinked => MacNodeRules.NoNodeLinkedReason,
        MacNodeState.Refused => "the Mac node was refused: " + (Reason ?? "its identity key did not match the pin"),
        MacNodeState.Connecting => "the Mac node is still connecting",
        _ => "the Mac node is not connected" + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : ": " + Reason),
    };
}

/// <summary>Which kind of guest a node command is about.</summary>
public enum MacGuestKind
{
    /// <summary>An iOS Simulator device. A macOS process, not a VM.</summary>
    IosSimulator,

    /// <summary>A macOS virtual machine (Virtualization.framework).</summary>
    MacOsVm,
}

/// <summary>Sizing and parent of a new guest on the node.</summary>
/// <param name="ParentRef">The pinned simulator runtime id, or the pinned restore image id.</param>
/// <param name="DeviceType">The simulator device type id for an iOS guest; null for a macOS VM.</param>
/// <param name="Vcpu">Virtual CPUs for a macOS VM; ignored for the simulator.</param>
/// <param name="MemoryMb">Memory for a macOS VM; ignored for the simulator.</param>
/// <param name="DiskCapGb">Disk cap for a macOS VM; ignored for the simulator.</param>
public sealed record MacGuestSpec(string ParentRef, string? DeviceType, int Vcpu, int MemoryMb, int DiskCapGb);

/// <summary>
/// A typed command for one guest (doc 62 section 6, "Commands"). The node accepts nothing else: never a shell string, never a path
/// outside its jail. Every variable part is validated by the sender before it goes on the link and again by the node.
/// </summary>
public abstract record MacGuestCommand
{
    /// <summary>Installs an app from the agent's workspace. The link moves the file as a copy-in with a hash check.</summary>
    /// <param name="WorkspaceRelativePath">Relative path inside the agent's workspace share; see <see cref="DeviceIdentifiers.WorkspaceRelativePath"/>.</param>
    public sealed record InstallApp(string WorkspaceRelativePath) : MacGuestCommand;

    /// <summary>Launches an installed app by its bundle id.</summary>
    public sealed record LaunchApp(string AppId) : MacGuestCommand;

    /// <summary>Sends a tap, key or text to the guest.</summary>
    public sealed record Input(DeviceInput Value) : MacGuestCommand;
}

/// <summary>The node is not linked, not connected or was refused. The message is a plain reason that names no address, key or path.</summary>
public sealed class MacNodeUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The link from the primary server to a headless Bazaar node on a Mac (doc 62 section 6). The node connects out, with mutual
/// authentication by pinned keys; this contract is what the iOS and macOS provider modules use, so cloud CI can test them against
/// <see cref="InMemoryMacNodeLink"/> and make no Apple call.
/// <para>
/// Apple rule: iOS and macOS guests run only on Apple hardware (see <see cref="MacNodeRules"/>). A link implementation talks to a
/// Mac the operator owns; nothing here emulates one.
/// </para>
/// <para>
/// Every operation fails with <see cref="MacNodeUnavailableException"/> while the node is not connected. Nothing is queued.
/// </para>
/// </summary>
public interface IMacNodeLink
{
    /// <summary>The last known state. Synchronous and never blocks, so a provider's cheap probe can read it.</summary>
    MacNodeStatus Status { get; }

    /// <summary>Establishes the link. Does nothing when already connected. A key mismatch leaves the state <see cref="MacNodeState.Refused"/>.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Drops the link. Guests keep their node-side state.</summary>
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>Creates a stopped guest and returns the node's reference for it (a simulator udid or a VM bundle name).</summary>
    Task<string> CreateGuestAsync(MacGuestKind kind, MacGuestSpec spec, CancellationToken ct);

    /// <summary>Launches the guest: boots the simulator or starts the VM. Nothing runs before this call.</summary>
    Task LaunchGuestAsync(MacGuestKind kind, string nodeRef, CancellationToken ct);

    /// <summary>Stops the guest, gracefully or by power cut.</summary>
    Task StopGuestAsync(MacGuestKind kind, string nodeRef, bool force, CancellationToken ct);

    /// <summary>Deletes the guest and its node-side state.</summary>
    Task DeleteGuestAsync(MacGuestKind kind, string nodeRef, CancellationToken ct);

    /// <summary>Whether the guest is running on the node now.</summary>
    Task<bool> IsGuestRunningAsync(MacGuestKind kind, string nodeRef, CancellationToken ct);

    /// <summary>Runs one typed command in a running guest.</summary>
    Task RunCommandAsync(MacGuestKind kind, string nodeRef, MacGuestCommand command, CancellationToken ct);

    /// <summary>Captures the guest's screen as PNG. The frame is untrusted external content (doc 59 rule 1).</summary>
    Task<DeviceScreen> ScreenshotAsync(MacGuestKind kind, string nodeRef, CancellationToken ct);
}

/// <summary>
/// The link of an install with no Mac node: always <see cref="MacNodeState.Unlinked"/>, and every operation refuses. The iOS and macOS
/// components register this when nothing else has, so their catalogue entries report "no Mac node linked" and never throw.
/// </summary>
public sealed class UnlinkedMacNodeLink : IMacNodeLink
{
    public MacNodeStatus Status => MacNodeStatus.Unlinked;

    public Task ConnectAsync(CancellationToken ct) => Refuse();

    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string> CreateGuestAsync(MacGuestKind kind, MacGuestSpec spec, CancellationToken ct) => Refuse<string>();

    public Task LaunchGuestAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Refuse();

    public Task StopGuestAsync(MacGuestKind kind, string nodeRef, bool force, CancellationToken ct) => Refuse();

    public Task DeleteGuestAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Refuse();

    public Task<bool> IsGuestRunningAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Refuse<bool>();

    public Task RunCommandAsync(MacGuestKind kind, string nodeRef, MacGuestCommand command, CancellationToken ct) => Refuse();

    public Task<DeviceScreen> ScreenshotAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Refuse<DeviceScreen>();

    private static Task Refuse() => Task.FromException(new MacNodeUnavailableException(MacNodeRules.NoNodeLinkedReason));

    private static Task<T> Refuse<T>() => Task.FromException<T>(new MacNodeUnavailableException(MacNodeRules.NoNodeLinkedReason));
}
