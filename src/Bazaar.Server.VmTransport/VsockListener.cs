using Bazaar.Vms.Sockets;

namespace Bazaar.Server.VmTransport;

/// <summary>Settings of the vsock listener.</summary>
/// <param name="Port">The vsock port guests dial. The shipped default matches the default Hyper-V service id.</param>
/// <param name="Backlog">Pending connections the OS queues.</param>
public sealed record VsockListenerOptions(uint Port = HvSocketAddress.DefaultPort, int Backlog = 16);

/// <summary>
/// Accepts guest connections on AF_VSOCK on a Linux host (KVM, later). The peer CID is the one the kernel reports, so a guest cannot claim another
/// VM. Callers wait for a CID, written as a decimal string.
/// </summary>
public sealed class VsockListener : PeerMatchedListener
{
    /// <summary>
    /// Creates a listener. Without <paramref name="acceptor"/> the real socket layer is used, which needs Linux with vsock.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">No acceptor was given and this host has no AF_VSOCK.</exception>
    public VsockListener(VsockListenerOptions? options = null, IPeerAcceptor? acceptor = null)
        : base(GuestTransportKind.Vsock, ChooseFactory(options ?? new VsockListenerOptions(), acceptor))
    {
    }

    /// <summary>True when this host can create an AF_VSOCK socket.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() && LinuxVsock.IsAvailable();

    /// <inheritdoc />
    protected override string Normalize(string vmId) => NormalizeCid(vmId);

    private static Func<IPeerAcceptor> ChooseFactory(VsockListenerOptions options, IPeerAcceptor? acceptor)
    {
        if (acceptor is not null)
        {
            return () => acceptor;
        }

        return IsSupported
            ? () => new VsockAcceptor(options)
            : throw new PlatformNotSupportedException("AF_VSOCK needs a Linux host with the vsock transport loaded.");
    }
}

/// <summary>The real AF_VSOCK accept loop over libc.</summary>
internal sealed class VsockAcceptor : IPeerAcceptor
{
    private const int _pollSliceMs = 200;
    private readonly LinuxVsock.FdHandle _listener;

    /// <summary>Binds to every CID on the port and starts listening.</summary>
    public VsockAcceptor(VsockListenerOptions options)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("AF_VSOCK needs a Linux host.");
        }

        _listener = LinuxVsock.Listen(options.Port, options.Backlog);
    }

    /// <inheritdoc />
    public ValueTask<AcceptedPeer> AcceptAsync(CancellationToken ct) =>
        new(Task.Run(
            () =>
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var accepted = LinuxVsock.Accept(_listener, _pollSliceMs);
                    if (accepted is var (handle, cid))
                    {
                        // The CID is the kernel's report of which VM connected, not anything the guest sent.
                        return new AcceptedPeer(cid.ToString(System.Globalization.CultureInfo.InvariantCulture), new VsockStream(handle));
                    }
                }
            },
            ct));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}
