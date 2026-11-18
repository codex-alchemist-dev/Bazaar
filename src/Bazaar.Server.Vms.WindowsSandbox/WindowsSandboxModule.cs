using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// Identity and host requirements of the Windows Sandbox VM provider (doc 50). The requirements are typed metadata:
/// the provider checks them at selection, and the install path checks the same declaration (owner, 2026-11-16).
/// </summary>
public static class WindowsSandboxModule
{
    /// <summary>The kind name the <c>host.provider.vm</c> setting selects.</summary>
    public const string Kind = "windows_sandbox";

    /// <summary>Windows Sandbox needs x64 Windows with the optional feature enabled (name to verify on the owner's PC, doc 50 section 5).</summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["windows"],
        ["x64"],
        ["windows-sandbox"]);

    /// <summary>
    /// What this provider can and cannot do, as the status page and the lifecycle service read it. Each value is backed by
    /// <see cref="WindowsSandboxBackend"/>: an unsupported operation throws, and a supported one works.
    /// </summary>
    public static WindowsSandboxCapabilities Capabilities { get; } = new(
        Save: false,
        Checkpoint: false,
        Restore: false,
        Headless: false,
        MaxConcurrentInstances: 1,
        DiskCapEnforced: false,
        ParentContentPinned: false,
        LinuxGuests: false,
        Ephemeral: true,
        MemoryConfigurable: true);
}

/// <summary>
/// What the Windows Sandbox provider supports. <see cref="MaxConcurrentInstances"/> is the host-wide limit to verify on the owner's PC
/// (doc 50 section 6). <see cref="DiskCapEnforced"/> false means <c>vm.windows.disk_cap_gb</c> cannot be honoured.
/// </summary>
public sealed record WindowsSandboxCapabilities(
    bool Save,
    bool Checkpoint,
    bool Restore,
    bool Headless,
    int MaxConcurrentInstances,
    bool DiskCapEnforced,
    bool ParentContentPinned,
    bool LinuxGuests,
    bool Ephemeral,
    bool MemoryConfigurable);
