using System.Net.Sockets;
using Bazaar.Vms.Sockets;

namespace Bazaar.Server.VmTransport;

/// <summary>Settings of the Hyper-V socket listener.</summary>
/// <param name="ServiceId">The service GUID guests dial. The setting <c>vm.transport.service_guid</c> will feed it; the default is a fixed GUID.</param>
/// <param name="Backlog">Pending connections the OS queues.</param>
/// <param name="BindVmId">
/// The one VM to listen for. The default, <see cref="Guid.Empty"/>, listens for every VM. A Windows Sandbox guest connects only to a listener
/// bound to its own VM id (measured on a Windows 11 host: a wildcard listener was refused), so the sandbox wiring sets it. A listener bound to one
/// VM still accepts only connections the hypervisor reports as coming from that VM.
/// </param>
public sealed record HvSocketListenerOptions(Guid ServiceId, int Backlog = 16, Guid BindVmId = default)
{
    /// <summary>The shipped default: vsock port 5000 in the Hyper-V service template, so Windows and Linux guests reach the same listener.</summary>
    public static HvSocketListenerOptions Default { get; } = new(HvSocketAddress.DefaultServiceId);
}

/// <summary>
/// Accepts guest connections on a Hyper-V socket (AF_HYPERV) on a Windows host. The peer VM id is the one the hypervisor reports for the
/// connection, so a guest cannot claim another VM. Linux guests on Hyper-V reach it through their vsock stack. The service GUID must be registered
/// under <c>GuestCommunicationServices</c> on the host before a guest can connect (spike S8 runbook).
/// </summary>
public sealed class HvSocketListener : PeerMatchedListener
{
    /// <summary>
    /// Creates a listener. Without <paramref name="acceptor"/> the real socket layer is used, which needs Windows.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">No acceptor was given and the host is not Windows.</exception>
    public HvSocketListener(HvSocketListenerOptions? options = null, IPeerAcceptor? acceptor = null)
        : base(GuestTransportKind.HvSocket, ChooseFactory(options ?? HvSocketListenerOptions.Default, acceptor))
    {
    }

    /// <summary>True when this host can run a real Hyper-V socket listener.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    protected override string Normalize(string vmId) => NormalizeGuid(vmId);

    private static Func<IPeerAcceptor> ChooseFactory(HvSocketListenerOptions options, IPeerAcceptor? acceptor)
    {
        if (acceptor is not null)
        {
            return () => acceptor;
        }

        return IsSupported
            ? () => new HvSocketAcceptor(options)
            : throw new PlatformNotSupportedException("Hyper-V sockets need a Windows host.");
    }
}

/// <summary>The real AF_HYPERV accept loop. Built by hand because .NET has no endpoint type for the family.</summary>
internal sealed class HvSocketAcceptor : IPeerAcceptor
{
    private readonly Socket _listener;
    private readonly Guid _boundVmId;

    /// <summary>Binds to the configured VM (every VM by default) and the given service, and starts listening.</summary>
    public HvSocketAcceptor(HvSocketListenerOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Hyper-V sockets need a Windows host.");
        }

        _boundVmId = options.BindVmId;
        _listener = new Socket((AddressFamily)HvSocketAddress.Family, SocketType.Stream, (ProtocolType)HvSocketAddress.ProtocolRaw);
        try
        {
            _listener.Bind(new HvSocketEndPoint(options.BindVmId == Guid.Empty ? HvSocketAddress.WildcardVmId : options.BindVmId, options.ServiceId));
            _listener.Listen(options.Backlog);
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<AcceptedPeer> AcceptAsync(CancellationToken ct)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(ct);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Interrupted)
            {
                // One client went away during accept. The listener itself is fine.
                continue;
            }

            try
            {
                // The peer address is the hypervisor's report of which VM connected, not anything the guest sent.
                if (socket.RemoteEndPoint is HvSocketEndPoint peer && peer.VmId != Guid.Empty && (_boundVmId == Guid.Empty || peer.VmId == _boundVmId))
                {
                    return new AcceptedPeer(peer.VmId.ToString("D"), new NetworkStream(socket, ownsSocket: true));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or ArgumentException)
            {
                // This one connection is unusable. Drop it and keep listening.
            }

            socket.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}
