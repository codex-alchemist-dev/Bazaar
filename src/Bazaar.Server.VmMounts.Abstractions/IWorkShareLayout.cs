namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>
/// PROPOSED (doc 16 WP8, not yet in a design doc): read-only host-side layout of one agent's two shares, derived from the same
/// <c>AgentsRoot</c> rules the mount manager enforces. The two folders never overlap: neither is the other or inside the other.
/// Paths are as configured (not symlink-resolved); the manager mounts the resolved equivalent.
/// </summary>
public interface IWorkShareLayout
{
    /// <summary>The agent's workspace share root (coding worktrees; read-write for the guest).</summary>
    string WorkShareRoot(Guid agentId);

    /// <summary>The agent's Soul/Passport folder (metadata and the cookie profile; read-only for the guest).</summary>
    string PassportFolder(Guid agentId);
}
