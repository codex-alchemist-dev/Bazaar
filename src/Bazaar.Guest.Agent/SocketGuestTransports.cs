using System.Net.Sockets;
using Bazaar.Vms.Sockets;

namespace Bazaar.Guest.Agent;

/// <summary>Which byte channel the agent talks to the host on.</summary>
public enum GuestTransportKind
{
    /// <summary>The process's stdin and stdout. The default, and what tests and Windows Sandbox use.</summary>
    Stdio,

    /// <summary>Hyper-V socket (AF_HYPERV) to the parent partition. Windows guests.</summary>
    HvSocket,

    /// <summary>AF_VSOCK to the host CID. Linux guests.</summary>
    Vsock,
}

/// <summary>Connect helper shared by the socket transports: the guest dials out, and retries until the host is listening.</summary>
internal static class GuestDial
{
    private static readonly TimeSpan _retryGap = TimeSpan.FromSeconds(1);

    /// <summary>Calls <paramref name="dial"/> until it succeeds or <paramref name="timeout"/> passes. Only I/O and socket failures are retried.</summary>
    /// <exception cref="IOException">The host could not be reached in time. The last failure is the inner exception.</exception>
    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> dial, TimeSpan timeout, TextWriter log, CancellationToken ct)
    {
        using var window = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, window.Token);
        Exception? last = null;
        while (true)
        {
            try
            {
                return await dial(linked.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                last = ex;
                log.WriteLine($"Host not reachable yet: {ex.Message}");
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new IOException($"The host could not be reached within {timeout.TotalSeconds:0} seconds.", last);
            }

            try
            {
                await Task.Delay(_retryGap, linked.Token);
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new IOException($"The host could not be reached within {timeout.TotalSeconds:0} seconds.", last);
            }
        }
    }
}

/// <summary>
/// Transport over a Hyper-V socket to the host (parent partition), for Windows guests. The guest dials out; the host listens on the service GUID.
/// </summary>
public sealed class HvSocketGuestTransport : IGuestTransport, IDisposable
{
    private readonly Stream _stream;

    private HvSocketGuestTransport(Stream stream)
    {
        _stream = stream;
    }

    /// <inheritdoc />
    public Stream FromHost => _stream;

    /// <inheritdoc />
    public Stream ToHost => _stream;

    /// <summary>Dials the host's service, retrying until <paramref name="timeout"/> passes.</summary>
    /// <exception cref="PlatformNotSupportedException">The guest is not Windows.</exception>
    /// <exception cref="IOException">The host did not accept in time.</exception>
    public static async Task<HvSocketGuestTransport> ConnectAsync(Guid serviceId, TimeSpan timeout, TextWriter log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Hyper-V sockets need a Windows guest. Use --transport vsock on Linux.");
        }

        var stream = await GuestDial.RetryAsync(
            async token =>
            {
                var socket = new Socket((AddressFamily)HvSocketAddress.Family, SocketType.Stream, (ProtocolType)HvSocketAddress.ProtocolRaw);
                try
                {
                    await socket.ConnectAsync(new HvSocketEndPoint(HvSocketAddress.ParentVmId, serviceId), token);
                    return (Stream)new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            timeout,
            log,
            ct);
        return new HvSocketGuestTransport(stream);
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}

/// <summary>Transport over AF_VSOCK to the host, for Linux guests. The guest dials out; the host listens on the port.</summary>
public sealed class VsockGuestTransport : IGuestTransport, IDisposable
{
    private readonly Stream _stream;

    private VsockGuestTransport(Stream stream)
    {
        _stream = stream;
    }

    /// <inheritdoc />
    public Stream FromHost => _stream;

    /// <inheritdoc />
    public Stream ToHost => _stream;

    /// <summary>Dials the host CID and port, retrying until <paramref name="timeout"/> passes.</summary>
    /// <exception cref="PlatformNotSupportedException">The guest is not Linux.</exception>
    /// <exception cref="IOException">The host did not accept in time.</exception>
    public static async Task<VsockGuestTransport> ConnectAsync(uint hostCid, uint port, TimeSpan timeout, TextWriter log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("AF_VSOCK needs a Linux guest. Use --transport hvsock on Windows.");
        }

        var stream = await GuestDial.RetryAsync(
            token => Task.Run(() => (Stream)new VsockStream(LinuxVsock.Connect(hostCid, port)), token),
            timeout,
            log,
            ct);
        return new VsockGuestTransport(stream);
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}
