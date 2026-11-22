using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmTransport;

/// <summary>
/// Whether a channel is still open. <see cref="IGuestChannel"/> itself has no such member (adding one would break every fake), so a channel
/// that is to be kept in the directory must also implement this. A channel that does not is never registered (fail closed).
/// </summary>
public interface IGuestChannelLifetime
{
    /// <summary>True only while the channel is open. Becomes false for good when it is closed, lost or faulted.</summary>
    bool IsOpen { get; }

    /// <summary>Completes when the channel has ended, in any way. Never faults.</summary>
    Task Completion { get; }
}

/// <summary>One agent's guest: the VM, and the open channel into it.</summary>
/// <param name="AgentId">The owning agent, as a lower-case "D"-format GUID string.</param>
/// <param name="VmId">The environment VM id (<c>environment_vm.id</c>).</param>
/// <param name="VmName">The VM's backend ref name; it has passed the strict VM-name pattern (a plain name, no separators or spaces).</param>
/// <param name="Os">The guest OS from the VM record.</param>
/// <param name="Channel">The open channel. It may end at any moment after it is returned; callers handle <see cref="GuestChannelException"/>.</param>
public sealed record AgentGuest(string AgentId, Guid VmId, string VmName, GuestOs Os, IGuestChannel Channel);

/// <summary>
/// Finds the open guest channel of an agent's VM (doc 16, "Channel directory"; PROPOSED). A channel is a path into an agent's VM, so a
/// result is returned only when all of these hold: the channel opened after a Hello that matched the VM, the channel is still open, the VM
/// record says the VM belongs to that agent and is not retired, and its name and OS match what was registered. Anything else is null or
/// absent from the list, never a stale channel.
/// </summary>
/// <remarks>
/// Agent ids are strings here because other modules use strings; the value must be a GUID (any format <see cref="Guid.TryParse(string?, out Guid)"/>
/// accepts). A value that is not one finds nothing.
/// </remarks>
public interface IGuestChannelDirectory
{
    /// <summary>The agent's open guest for one OS, or null when it has none, it is not served, or the agent has more than one live VM of that OS.</summary>
    ValueTask<AgentGuest?> TryGetAsync(string agentId, GuestOs os, CancellationToken ct);

    /// <summary>
    /// The agent's open guests, ordered by <see cref="GuestOs"/> (enum order) and then by VM name, ordinal. Empty when none. A VM whose channel is
    /// down is not in the list; use <see cref="IAgentVmLookup"/> for the VMs themselves.
    /// </summary>
    ValueTask<IReadOnlyList<AgentGuest>> ListAsync(string agentId, CancellationToken ct);

    /// <summary>The open channel of one VM, under the same checks as <see cref="TryGetAsync"/>, or null.</summary>
    ValueTask<IGuestChannel?> TryGetByVmAsync(Guid vmId, CancellationToken ct);
}

/// <summary>One VM of an agent as the VM store records it.</summary>
/// <param name="VmId">The environment VM id.</param>
/// <param name="Os">The guest OS.</param>
/// <param name="VmName">The backend ref name, or null when the VM has none yet (not created).</param>
/// <param name="Retired">True when the VM is retired (or otherwise no longer a live VM of the agent).</param>
/// <param name="GuestInterface">Linux host: the bridge port (tap or veth) name of the guest; null when the record does not carry one.</param>
public sealed record AgentVmInfo(Guid VmId, GuestOs Os, string? VmName, bool Retired, string? GuestInterface = null);

/// <summary>
/// Port the host implements over the VM store: the VMs of one agent, retired ones included. The default, <see cref="NoAgentVmLookup"/>, knows
/// none, so the directory serves nothing and the egress resolver refuses.
/// </summary>
public interface IAgentVmLookup
{
    /// <summary>All VMs the store has for the agent. Empty for an unknown agent.</summary>
    ValueTask<IReadOnlyList<AgentVmInfo>> ListAsync(Guid agentId, CancellationToken ct);
}

/// <summary>The shipped <see cref="IAgentVmLookup"/>: no agent has a VM. Fails closed.</summary>
public sealed class NoAgentVmLookup : IAgentVmLookup
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentVmInfo>> ListAsync(Guid agentId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AgentVmInfo>>([]);
}
