using System.Net;
using System.Text.RegularExpressions;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Providers.Smb;

/// <summary>
/// The local account a VM's guest uses to open one share. <see cref="AccountName"/> is spelled <c>HOST\name</c>, as Windows reads it
/// back from the share ACL, so the attach script can compare it exactly. It names an account only: no password, key or token is part of
/// this port (the guest-side logon is the identity service's concern).
/// </summary>
public sealed record VmShareAccount(string AccountName);

/// <summary>
/// Provides the per-VM local account that may open a share (doc 16: "bound to a per-agent local account on the host"; the workspace share
/// has its own account, separate from the Soul share, so <paramref name="kind"/> is part of the request). Implementations create the
/// account if needed and hand back its name only. Secrets are never returned, stored in settings, or logged by this module.
/// </summary>
public interface IVmShareIdentity
{
    Task<VmShareAccount> EnsureAccountAsync(Guid agentId, Guid vmId, MountKind kind, CancellationToken ct);
}

/// <summary>The host address of the agent's virtual switch, the only address a share is meant to be reachable on.</summary>
public interface IVmShareEndpoint
{
    Task<IPAddress> GetHostAddressAsync(Guid agentId, Guid vmId, CancellationToken ct);
}

/// <summary>The shipped <see cref="IVmShareIdentity"/>: it refuses, so an SMB attach fails until the host supplies a real one.</summary>
public sealed class UnavailableVmShareIdentity : IVmShareIdentity
{
    public Task<VmShareAccount> EnsureAccountAsync(Guid agentId, Guid vmId, MountKind kind, CancellationToken ct) =>
        throw new InvalidOperationException("No IVmShareIdentity is registered: no per-VM share account exists, so no SMB share is created.");
}

/// <summary>The shipped <see cref="IVmShareEndpoint"/>: it refuses, so an SMB attach fails until the host supplies the switch address.</summary>
public sealed class UnavailableVmShareEndpoint : IVmShareEndpoint
{
    public Task<IPAddress> GetHostAddressAsync(Guid agentId, Guid vmId, CancellationToken ct) =>
        throw new InvalidOperationException("No IVmShareEndpoint is registered: the agent's virtual switch address is unknown, so no SMB share is created.");
}

/// <summary>Share and account names. Every name that reaches a script is built here or matched against these patterns.</summary>
internal static partial class SmbShareNames
{
    /// <summary>Fixed prefix of every share this module creates. Reconcile never touches a share without it.</summary>
    public const string Prefix = "bazaar-";

    [GeneratedRegex(@"^bazaar-[0-9a-f]{12}-(soul|work)-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SharePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,63}\\[A-Za-z0-9._-]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountPattern();

    /// <summary><c>bazaar-&lt;agent tag&gt;-&lt;soul|work&gt;-&lt;mount id&gt;</c>. The mount id makes it unique and lets a restart find it again.</summary>
    public static string For(Guid agentId, MountKind kind, Guid mountId)
    {
        if (agentId == Guid.Empty || mountId == Guid.Empty)
            throw new ArgumentException("The agent and mount ids must be set.");
        var name = Prefix + agentId.ToString("N")[..12]
            + (kind == MountKind.Soul ? "-soul-" : "-work-")
            + mountId.ToString("N");
        return IsValidShare(name) ? name : throw new ArgumentException("The share name is not valid.");
    }

    public static bool IsValidShare(string? name) => name is not null && SharePattern().IsMatch(name);

    public static bool IsValidAccount(string? name) => name is not null && AccountPattern().IsMatch(name);
}
