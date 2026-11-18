using System.Globalization;
using Bazaar.Vms.Sockets;

namespace Bazaar.Server.VmTransport;

/// <summary>One connection a transport accepted, with the peer id the transport itself reported.</summary>
/// <param name="PeerId">The peer VM id, normalised the way the listener normalises the id a caller waits for.</param>
/// <param name="Stream">The duplex stream. The listener owns it until it hands it to a waiter or closes it.</param>
public sealed record AcceptedPeer(string PeerId, Stream Stream);

/// <summary>The socket layer under a listener. A fake one lets tests drive peers the real sockets cannot produce.</summary>
public interface IPeerAcceptor : IAsyncDisposable
{
    /// <summary>Waits for the next connection from any peer.</summary>
    ValueTask<AcceptedPeer> AcceptAsync(CancellationToken ct);
}

/// <summary>
/// A listener that accepts from any peer and hands each connection only to the caller waiting for that peer's id. A connection from a peer nobody
/// waits for is closed at once, so a guest that is not expected, or that dials early, cannot hold a descriptor. Guests retry until the host waits.
/// The accept loop starts on the first wait, so constructing a listener binds nothing.
/// </summary>
public abstract class PeerMatchedListener : IGuestConnectionListener
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);

    private readonly Func<IPeerAcceptor> _acceptorFactory;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<GuestConnection>> _waiters = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>Creates a listener over a lazily built acceptor.</summary>
    protected PeerMatchedListener(GuestTransportKind kind, Func<IPeerAcceptor> acceptorFactory)
    {
        ArgumentNullException.ThrowIfNull(acceptorFactory);
        Kind = kind;
        _acceptorFactory = acceptorFactory;
    }

    /// <inheritdoc />
    public GuestTransportKind Kind { get; }

    /// <inheritdoc />
    public ValueTask<GuestConnection> AcceptAsync(string vmId, CancellationToken ct)
    {
        var key = Normalize(vmId);
        var waiter = new TaskCompletionSource<GuestConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<GuestConnection>? replaced;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _waiters.TryGetValue(key, out replaced);
            _waiters[key] = waiter;
            _loop ??= Task.Run(() => AcceptLoopAsync(_stop.Token), CancellationToken.None);
        }

        replaced?.TrySetCanceled(CancellationToken.None);
        var registration = ct.Register(() =>
        {
            lock (_gate)
            {
                if (_waiters.TryGetValue(key, out var current) && ReferenceEquals(current, waiter))
                {
                    _waiters.Remove(key);
                }
            }

            waiter.TrySetCanceled(ct);
        });
        return new ValueTask<GuestConnection>(WaitAsync(waiter.Task, registration));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        TaskCompletionSource<GuestConnection>[] waiters;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            waiters = [.. _waiters.Values];
            _waiters.Clear();
        }

        await _stop.CancelAsync();
        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled();
        }

        if (loop is not null)
        {
            await loop;
        }

        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Turns the id a caller waits for into the form <see cref="AcceptedPeer.PeerId"/> uses.</summary>
    /// <exception cref="ArgumentException">The id is not valid for this transport.</exception>
    protected abstract string Normalize(string vmId);

    private static async Task<GuestConnection> WaitAsync(Task<GuestConnection> waiter, CancellationTokenRegistration registration)
    {
        using (registration)
        {
            return await waiter;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken stop)
    {
        IPeerAcceptor? acceptor = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    acceptor ??= _acceptorFactory();
                    var peer = await acceptor.AcceptAsync(stop);
                    await OfferAsync(peer);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
                {
                    // The socket layer failed. Drop it and rebuild it after a pause, so a transient fault does not end the listener.
                    if (acceptor is not null)
                    {
                        await acceptor.DisposeAsync();
                        acceptor = null;
                    }

                    await Task.Delay(_retryDelay, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            if (acceptor is not null)
            {
                await acceptor.DisposeAsync();
            }
        }
    }

    private async ValueTask OfferAsync(AcceptedPeer peer)
    {
        TaskCompletionSource<GuestConnection>? waiter;
        lock (_gate)
        {
            _waiters.Remove(peer.PeerId, out waiter);
        }

        if (waiter is not null && waiter.TrySetResult(new GuestConnection(peer.PeerId, Kind, peer.Stream)))
        {
            return;
        }

        await peer.Stream.DisposeAsync();
    }

    /// <summary>Parses a Hyper-V VM id (a GUID) into its canonical lower-case form.</summary>
    internal static string NormalizeGuid(string vmId) =>
        Guid.TryParse(vmId, out var guid) && guid != Guid.Empty
            ? guid.ToString("D")
            : throw new ArgumentException($"'{vmId}' is not a VM GUID.", nameof(vmId));

    /// <summary>Parses a vsock CID into its canonical decimal form.</summary>
    internal static string NormalizeCid(string vmId) =>
        uint.TryParse(vmId, NumberStyles.None, CultureInfo.InvariantCulture, out var cid) && cid > LinuxVsock.HostCid
            ? cid.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException($"'{vmId}' is not a guest CID.", nameof(vmId));
}
