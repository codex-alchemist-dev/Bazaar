namespace Bazaar.Guest.Agent;

/// <summary>
/// The byte channel between the guest agent and the host. The default uses stdin and stdout
/// (<see cref="StreamGuestTransport"/>). Hyper-V sockets (<see cref="HvSocketGuestTransport"/>, Windows guests) and AF_VSOCK
/// (<see cref="VsockGuestTransport"/>, Linux guests) dial out to the host and hand back the socket's two directions.
/// </summary>
public interface IGuestTransport
{
    /// <summary>Bytes from the host.</summary>
    Stream FromHost { get; }

    /// <summary>Bytes to the host.</summary>
    Stream ToHost { get; }
}

/// <summary>Transport over two existing streams, such as the process's stdin and stdout.</summary>
public sealed class StreamGuestTransport : IGuestTransport
{
    /// <summary>Creates a transport over the given streams. The agent does not own or dispose them.</summary>
    public StreamGuestTransport(Stream fromHost, Stream toHost)
    {
        ArgumentNullException.ThrowIfNull(fromHost);
        ArgumentNullException.ThrowIfNull(toHost);
        FromHost = fromHost;
        ToHost = toHost;
    }

    /// <inheritdoc />
    public Stream FromHost { get; }

    /// <inheritdoc />
    public Stream ToHost { get; }
}
