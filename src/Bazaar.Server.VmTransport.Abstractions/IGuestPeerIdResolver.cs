namespace Bazaar.Server.VmTransport;

/// <summary>
/// Maps a running VM to the peer id its transport reports for it. A Hyper-V socket listener names a peer by the hypervisor's VM GUID and a vsock
/// listener by the guest CID, while the VM store records a backend ref name, so the VM backend that knows the mapping supplies it. Without a
/// resolver the opener uses the backend ref as the peer id, which is right only when the two are the same string (the loopback listener).
/// </summary>
public interface IGuestPeerIdResolver
{
    /// <summary>The peer id for <paramref name="target"/>, or null when the hypervisor does not know the VM yet. Never a secret: it may be logged.</summary>
    ValueTask<string?> ResolveAsync(GuestOpenTarget target, CancellationToken ct);
}
