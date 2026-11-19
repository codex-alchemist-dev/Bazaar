namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class HyperVSocketEndPointTests
{
    private static readonly Guid _vm = new("11112222-3333-4444-5555-666677778888");

    [Fact]
    public void The_service_id_carries_the_port_in_its_first_eight_hex_digits()
    {
        Assert.Equal(new Guid("1a2b3c4d-facb-11e6-bd58-64006a7986d3"), HyperVSocketEndPoint.ServiceIdForPort(0x1A2B3C4D));
        Assert.Equal(new Guid("00000050-facb-11e6-bd58-64006a7986d3"), HyperVSocketEndPoint.ServiceIdForPort(0x50));
    }

    [Fact]
    public void The_address_is_36_bytes_with_the_vm_id_then_the_service_id()
    {
        var service = HyperVSocketEndPoint.ServiceIdForPort(0x1A2B3C4D);

        var address = new HyperVSocketEndPoint(_vm, service).ToBytes();

        Assert.Equal(HyperVSocketEndPoint.SocketAddressSize, address.Length);
        Assert.Equal(34, address[0]);
        Assert.Equal(0, address[1]);
        Assert.Equal(0, address[2]);
        Assert.Equal(0, address[3]);
        Assert.Equal(_vm.ToByteArray(), address[4..20]);
        Assert.Equal(service.ToByteArray(), address[20..36]);
    }

    [Fact]
    public void An_address_round_trips()
    {
        var endpoint = new HyperVSocketEndPoint(_vm, HyperVSocketEndPoint.ServiceIdForPort(7));

        var back = (HyperVSocketEndPoint.FromBytes(endpoint.ToBytes()));

        Assert.Equal(endpoint.VmId, back.VmId);
        Assert.Equal(endpoint.ServiceId, back.ServiceId);
    }

    [Fact]
    public void An_address_of_the_wrong_size_is_refused()
    {
        var endpoint = new HyperVSocketEndPoint(_vm, Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => HyperVSocketEndPoint.FromBytes(new byte[16]));
        Assert.Throws<ArgumentException>(() => HyperVSocketEndPoint.FromBytes(new byte[36]));
        Assert.NotNull(endpoint);
    }
}
