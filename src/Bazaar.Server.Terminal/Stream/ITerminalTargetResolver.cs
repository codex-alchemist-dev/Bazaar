using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Stream;

/// <summary>Where a new terminal is opened: the agent it belongs to and the VM that hosts the shell.</summary>
/// <param name="AgentId">Agent id the broker files the terminal under.</param>
/// <param name="VmId">VM that runs the shell.</param>
public sealed record TerminalTarget(string AgentId, string VmId);

/// <summary>
/// Maps a <c>TerminalOpen</c> frame's agent id to a VM. The open frame carries no VM id, so the wire host supplies this seam.
/// Authorization is still the broker's access policy; the resolver only locates the target.
/// </summary>
public interface ITerminalTargetResolver
{
    /// <summary>Resolves the target. <paramref name="agentId"/> is null for a human-owned VM shell. Null means "no such target" and is refused as not found.</summary>
    ValueTask<TerminalTarget?> ResolveAsync(TerminalCaller caller, string? agentId, CancellationToken ct);
}
