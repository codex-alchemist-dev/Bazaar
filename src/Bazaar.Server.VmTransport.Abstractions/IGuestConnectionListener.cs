namespace Bazaar.Server.VmTransport;

/// <summary>The kind of byte transport a guest connection arrived on.</summary>
public enum GuestTransportKind
{
    /// <summary>In-memory duplex, for tests.</summary>
    Loopback,

    /// <summary>Hyper-V socket (AF_HYPERV), Windows host.</summary>
    HvSocket,

    /// <summary>AF_VSOCK, Linux host.</summary>
    Vsock,
}

/// <summary>One accepted guest connection: a duplex stream and the VM the hypervisor says is on the other end.</summary>
public sealed class GuestConnection : IAsyncDisposable
{
    /// <summary>Creates a connection. Disposing it disposes <paramref name="stream"/>.</summary>
    public GuestConnection(string peerVmId, GuestTransportKind kind, Stream stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerVmId);
        ArgumentNullException.ThrowIfNull(stream);
        PeerVmId = peerVmId;
        Kind = kind;
        Stream = stream;
    }

    /// <summary>The VM id the transport reported for the peer, in the form the VM backend records as its backend ref.</summary>
    public string PeerVmId { get; }

    /// <summary>The transport this connection came in on.</summary>
    public GuestTransportKind Kind { get; }

    /// <summary>The duplex byte stream carrying guest frames.</summary>
    public Stream Stream { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Accepts connections that guests dial out to the host. The guest dials, never the host. A listener hands a connection only to the caller waiting
/// for exactly that VM, so a guest cannot claim another VM's id: the id comes from the hypervisor, not from the guest.
/// </summary>
public interface IGuestConnectionListener : IAsyncDisposable
{
    /// <summary>The transport this listener serves.</summary>
    GuestTransportKind Kind { get; }

    /// <summary>
    /// Waits for the guest of <paramref name="vmId"/> to connect and returns that connection. Connections from any other peer are closed.
    /// A second wait for the same VM replaces the first, which then fails with <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="vmId"/> is not a valid peer id for this transport.</exception>
    ValueTask<GuestConnection> AcceptAsync(string vmId, CancellationToken ct);
}
