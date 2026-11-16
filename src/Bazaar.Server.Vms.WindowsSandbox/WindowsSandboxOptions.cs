namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>How a sandbox's network is configured in its <c>.wsb</c> file.</summary>
public enum WindowsSandboxNetworking
{
    /// <summary>No network. The default: doc 16 requires default-deny egress, and this provider has no allowlist of its own.</summary>
    Disable,

    /// <summary>The sandbox NAT network. Only for a later spike that proves egress control on the host.</summary>
    Default,
}

/// <summary>
/// Settings for the provider. <see cref="HostBaseImage"/> is the one parent the provider accepts: Windows Sandbox has no
/// parent disk, so the "image" is the host's own Windows base, identified by an id the operator sets. Its contents are not pinned.
/// </summary>
/// <param name="HostBaseImage">The parent image id that <c>CreateChildAsync</c> accepts. Any other parent is refused.</param>
/// <param name="Networking">Network mode written into every sandbox config. Defaults to <see cref="WindowsSandboxNetworking.Disable"/>.</param>
public sealed record WindowsSandboxOptions(Guid HostBaseImage, WindowsSandboxNetworking Networking = WindowsSandboxNetworking.Disable);
