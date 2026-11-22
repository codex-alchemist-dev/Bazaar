using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.Vms.Sockets;

/// <summary>
/// The Hyper-V socket address layout (<c>SOCKADDR_HV</c>): family, reserved, VM id, service id. .NET has no endpoint type for
/// AF_HYPERV, so the bytes are built by hand. This file is also compiled into the guest agent, which has no reference to the server modules.
/// The pure encode and decode work on any OS; only <see cref="HvSocketEndPoint"/> needs the Windows socket stack.
/// </summary>
internal static class HvSocketAddress
{
    /// <summary>AF_HYPERV.</summary>
    public const int Family = 34;

    /// <summary>HV_PROTOCOL_RAW, the only protocol Hyper-V sockets offer.</summary>
    public const int ProtocolRaw = 1;

    /// <summary>Size of <c>SOCKADDR_HV</c> in bytes.</summary>
    public const int Size = 36;

    /// <summary>HV_GUID_WILDCARD: a listener bound to it accepts any VM.</summary>
    public static readonly Guid WildcardVmId = Guid.Empty;

    /// <summary>HV_GUID_PARENT: the id a guest connects to in order to reach its host.</summary>
    public static readonly Guid ParentVmId = new("a42e7cda-d03f-480c-9cc2-a4de20abb878");

    /// <summary>
    /// The default service id: the template Linux guests use to map a vsock port to a Hyper-V service, with port 5000. A Linux guest dialling
    /// vsock port 5000 therefore reaches the same host listener as a Windows guest dialling this GUID.
    /// </summary>
    public static readonly Guid DefaultServiceId = ServiceIdForPort(DefaultPort);

    /// <summary>The default vsock port, shared with the default service id.</summary>
    public const uint DefaultPort = 5000;

    /// <summary>The Hyper-V service id that a Linux guest's vsock <paramref name="port"/> maps to.</summary>
    public static Guid ServiceIdForPort(uint port) => new(port, 0xfacb, 0x11e6, 0xbd, 0x58, 0x64, 0x00, 0x6a, 0x79, 0x86, 0xd3);

    /// <summary>Writes the 36-byte address into <paramref name="destination"/>.</summary>
    public static void Encode(Guid vmId, Guid serviceId, Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"The buffer must hold {Size} bytes.", nameof(destination));
        }

        destination[..Size].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Family);
        vmId.TryWriteBytes(destination.Slice(4, 16));
        serviceId.TryWriteBytes(destination.Slice(20, 16));
    }

    /// <summary>Reads an address. Returns false when it is too short or is not an AF_HYPERV address.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, out Guid vmId, out Guid serviceId)
    {
        vmId = Guid.Empty;
        serviceId = Guid.Empty;
        if (source.Length < Size || BinaryPrimitives.ReadUInt16LittleEndian(source) != Family)
        {
            return false;
        }

        vmId = new Guid(source.Slice(4, 16));
        serviceId = new Guid(source.Slice(20, 16));
        return true;
    }
}

/// <summary>An AF_HYPERV endpoint for <see cref="Socket"/>. Only usable on Windows, where the family exists.</summary>
internal sealed class HvSocketEndPoint : EndPoint
{
    /// <summary>Creates an endpoint.</summary>
    public HvSocketEndPoint(Guid vmId, Guid serviceId)
    {
        VmId = vmId;
        ServiceId = serviceId;
    }

    /// <summary>The VM the endpoint names. For a peer address, the VM the hypervisor says is on the other end.</summary>
    public Guid VmId { get; }

    /// <summary>The service id.</summary>
    public Guid ServiceId { get; }

    /// <inheritdoc />
    public override AddressFamily AddressFamily => (AddressFamily)HvSocketAddress.Family;

    /// <inheritdoc />
    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, HvSocketAddress.Size);
        HvSocketAddress.Encode(VmId, ServiceId, address.Buffer.Span);
        return address;
    }

    /// <inheritdoc />
    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        return HvSocketAddress.TryDecode(socketAddress.Buffer.Span[..socketAddress.Size], out var vmId, out var serviceId)
            ? new HvSocketEndPoint(vmId, serviceId)
            : throw new ArgumentException("The address is not an AF_HYPERV address.", nameof(socketAddress));
    }
}
