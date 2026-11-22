using System.Text;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmTransport;

/// <summary>
/// One running VM the channel opener should open a guest channel to (doc 16 "Channel opener", PROPOSED).
/// </summary>
/// <param name="VmId">The environment VM id (<c>environment_vm.id</c>). The guest echoes it in its Hello.</param>
/// <param name="AgentId">The owning agent, a non-empty GUID in any format.</param>
/// <param name="BackendRef">The VM's backend ref name: what the transport reports as the peer id, and the name the registry checks.</param>
/// <param name="Os">The guest OS.</param>
/// <param name="Nonce">The server-issued secret the guest must echo in its Hello. Never log it, put it in an exception or in an audit record.</param>
public sealed record GuestOpenTarget(Guid VmId, string AgentId, string BackendRef, GuestOs Os, string Nonce)
{
    // The generated record printer would write every member, the nonce included. Leave it out, so a logged target cannot leak it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("VmId = ").Append(VmId).Append(", Os = ").Append(Os);
        return true;
    }
}

/// <summary>
/// The running VMs a guest channel should be opened to. The host implements it over the VM store; the opener never reads the store itself.
/// A VM that is not in the list is not opened to, so a host that cannot say which VMs run (or has no nonce for one) lists none.
/// </summary>
public interface IGuestOpenTargets
{
    /// <summary>Every VM that is running now and has a nonce. A VM that is not running is absent.</summary>
    ValueTask<IReadOnlyList<GuestOpenTarget>> ListRunningAsync(CancellationToken ct);

    /// <summary>One VM, or null when it is not running (or unknown, or has no nonce).</summary>
    ValueTask<GuestOpenTarget?> GetAsync(Guid vmId, CancellationToken ct);
}

/// <summary>
/// Where a VM's Hello nonce comes from: the secret the server issued when it provisioned the guest. Whatever delivers the nonce to the guest
/// (the backend that creates the VM) must also make it readable here. Nothing in the repository implements this yet (doc 16 "Channel opener").
/// </summary>
public interface IGuestNonceSource
{
    /// <summary>The nonce of one VM, or null when none was issued. The value is a secret: the caller must not log it.</summary>
    ValueTask<string?> GetAsync(Guid vmId, CancellationToken ct);
}
