using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// A Hyper-V socket address (AF_HYPERV), the guest channel for Windows Sandbox (doc 50 section 9, spike S-WSB step 9).
/// The wire form is a 36-byte sockaddr: family (2 bytes), reserved (2), VM id (16, <see cref="Guid.ToByteArray()"/> order), service id (16).
/// Pure data: building one touches no socket and no host state.
/// </summary>
public sealed class HyperVSocketEndPoint : EndPoint
{
    /// <summary>The address family number Windows gives AF_HYPERV.</summary>
    public const int HyperVFamily = 34;

    /// <summary>The size of the native address.</summary>
    public const int SocketAddressSize = 36;

    /// <summary>The VM id a guest uses to reach its host.</summary>
    public static readonly Guid ParentVmId = new("a42e7cda-d03f-480c-9cc2-a4de20abb878");

    private const string _serviceSuffix = "-FACB-11E6-BD58-64006A7986D3";

    /// <summary>Creates an endpoint for a VM id and a service id.</summary>
    public HyperVSocketEndPoint(Guid vmId, Guid serviceId)
    {
        VmId = vmId;
        ServiceId = serviceId;
    }

    /// <summary>The partition the endpoint belongs to. A host listener must use the sandbox's own id, not the wildcard.</summary>
    public Guid VmId { get; }

    /// <summary>The registered service the connection targets.</summary>
    public Guid ServiceId { get; }

    /// <inheritdoc />
    public override AddressFamily AddressFamily => (AddressFamily)HyperVFamily;

    /// <summary>Builds the service id for a vsock-style port, as Windows defines it: the port in the first eight hex digits.</summary>
    public static Guid ServiceIdForPort(uint port) =>
        Guid.Parse(port.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + _serviceSuffix);

    /// <summary>The native sockaddr bytes. Pure and OS-independent, so it is tested on every host.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[SocketAddressSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, HyperVFamily);
        VmId.TryWriteBytes(bytes.AsSpan(4, 16));
        ServiceId.TryWriteBytes(bytes.AsSpan(20, 16));
        return bytes;
    }

    /// <summary>Reads the native sockaddr bytes back into an endpoint.</summary>
    public static HyperVSocketEndPoint FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SocketAddressSize || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes) != HyperVFamily)
        {
            throw new ArgumentException($"A Hyper-V socket address is {SocketAddressSize} bytes of family {HyperVFamily}.", nameof(bytes));
        }

        return new HyperVSocketEndPoint(new Guid(bytes.Slice(4, 16)), new Guid(bytes.Slice(20, 16)));
    }

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">The host OS has no Hyper-V sockets.</exception>
    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, SocketAddressSize);
        var bytes = ToBytes();
        for (var i = 2; i < bytes.Length; i++)
        {
            address[i] = bytes[i];
        }

        return address;
    }

    /// <inheritdoc />
    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        if (socketAddress.Size != SocketAddressSize)
        {
            throw new ArgumentException($"A Hyper-V socket address is {SocketAddressSize} bytes.", nameof(socketAddress));
        }

        var bytes = new byte[SocketAddressSize];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = socketAddress[i];
        }

        return FromBytes(bytes);
    }
}
