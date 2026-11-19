using System.Runtime.InteropServices;
using Bazaar.Contracts.Vms;
using Bazaar.Guest.Agent;
using Bazaar.Vms.Sockets;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>
/// Address encoding, the descriptor stream, platform guards, and the real sockets. Real AF_HYPERV and AF_VSOCK need a Hyper-V host or a vsock
/// kernel, so those tests carry a Category trait and skip themselves where the OS lacks the family.
/// </summary>
public partial class SocketLevelTests
{
    [Fact]
    public void The_hyperv_address_has_the_sockaddr_hv_layout()
    {
        var vm = new Guid("11111111-2222-3333-4444-555555555555");
        var service = HvSocketAddress.ServiceIdForPort(5000);
        var bytes = new byte[HvSocketAddress.Size];

        HvSocketAddress.Encode(vm, service, bytes);

        Assert.Equal(36, bytes.Length);
        Assert.Equal(new byte[] { 34, 0, 0, 0 }, bytes.AsSpan(0, 4).ToArray());
        Assert.Equal(vm.ToByteArray(), bytes.AsSpan(4, 16).ToArray());
        Assert.Equal(service.ToByteArray(), bytes.AsSpan(20, 16).ToArray());
        Assert.True(HvSocketAddress.TryDecode(bytes, out var decodedVm, out var decodedService));
        Assert.Equal((vm, service), (decodedVm, decodedService));
    }

    [Fact]
    public void The_default_service_id_is_the_hyperv_template_for_port_5000_so_one_listener_serves_both_guest_kinds()
    {
        Assert.Equal(new Guid("00001388-facb-11e6-bd58-64006a7986d3"), HvSocketAddress.DefaultServiceId);
        Assert.Equal(HvSocketAddress.DefaultServiceId, HvSocketAddress.ServiceIdForPort(HvSocketAddress.DefaultPort));
        Assert.Equal(5000u, new VsockListenerOptions().Port);
        Assert.Equal(HvSocketAddress.DefaultServiceId, HvSocketListenerOptions.Default.ServiceId);
        Assert.Equal(Guid.Empty, HvSocketListenerOptions.Default.BindVmId);
        Assert.Equal(HvSocketAddress.DefaultServiceId, new GuestAgentOptions { VmId = "v", Nonce = "n", AllowedRoots = ["/"] }.HvSocketServiceId);
    }

    [Fact]
    public void A_hyperv_address_that_is_short_or_of_another_family_is_not_decoded()
    {
        var bytes = new byte[HvSocketAddress.Size];
        HvSocketAddress.Encode(Guid.NewGuid(), Guid.NewGuid(), bytes);

        Assert.False(HvSocketAddress.TryDecode(bytes.AsSpan(0, 35), out _, out _));
        bytes[0] = 2;
        Assert.False(HvSocketAddress.TryDecode(bytes, out _, out _));
        Assert.Throws<ArgumentException>(() => HvSocketAddress.Encode(Guid.Empty, Guid.Empty, new byte[10]));
    }

    [Fact]
    public void The_vsock_address_has_the_sockaddr_vm_layout()
    {
        var bytes = new byte[LinuxVsock.AddressSize];

        LinuxVsock.EncodeAddress(cid: 3, port: 5000, bytes);

        Assert.Equal([40, 0, 0, 0, 0x88, 0x13, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0], bytes);
        Assert.True(LinuxVsock.TryDecodeAddress(bytes, out var cid, out var port));
        Assert.Equal((3u, 5000u), (cid, port));
        Assert.False(LinuxVsock.TryDecodeAddress(bytes.AsSpan(0, 15), out _, out _));
        bytes[0] = 2;
        Assert.False(LinuxVsock.TryDecodeAddress(bytes, out _, out _));
    }

    [Fact]
    public void Listeners_without_a_fake_refuse_to_start_on_a_host_that_lacks_the_family()
    {
        if (!HvSocketListener.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new HvSocketListener());
        }

        if (!VsockListener.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new VsockListener());
        }
    }

    [Fact]
    public async Task The_guest_transport_for_the_other_os_fails_with_platform_not_supported()
    {
        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => HvSocketGuestTransport.ConnectAsync(HvSocketAddress.DefaultServiceId, TimeSpan.FromSeconds(1), TextWriter.Null, CancellationToken.None));
        }

        if (!OperatingSystem.IsLinux())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => VsockGuestTransport.ConnectAsync(2, 5000, TimeSpan.FromSeconds(1), TextWriter.Null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_guest_that_cannot_reach_the_host_gives_up_with_an_io_error_after_its_timeout()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The vsock dial is Linux only.");

        var error = await Assert.ThrowsAsync<IOException>(
            () => VsockGuestTransport.ConnectAsync(2, 5000, TimeSpan.FromMilliseconds(1500), TextWriter.Null, CancellationToken.None));

        Assert.Contains("could not be reached", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_descriptor_stream_carries_bytes_both_ways_and_ends_when_the_peer_closes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The descriptor stream uses libc.");
        var (left, right) = SocketPair();
        await using var a = new VsockStream(left);
        await using var b = new VsockStream(right);

        await a.WriteAsync(new byte[] { 1, 2, 3 });
        var buffer = new byte[3];
        await b.ReadExactlyAsync(buffer).AsTask().WithinAsync();
        await b.WriteAsync(new byte[] { 9 });
        var back = new byte[1];
        await a.ReadExactlyAsync(back).AsTask().WithinAsync();
        await a.DisposeAsync();
        var end = await b.ReadAsync(new byte[1]).AsTask().WithinAsync();

        Assert.Equal([1, 2, 3], buffer);
        Assert.Equal([9], back);
        Assert.Equal(0, end);
    }

    [Fact]
    public async Task The_descriptor_stream_moves_a_large_payload_in_order_through_its_pumps()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The descriptor stream uses libc.");
        var (left, right) = SocketPair();
        await using var a = new VsockStream(left);
        await using var b = new VsockStream(right);
        var payload = new byte[2 * 1024 * 1024];
        new Random(3).NextBytes(payload);

        var write = Task.Run(async () =>
        {
            for (var offset = 0; offset < payload.Length; offset += 50_000)
            {
                await a.WriteAsync(payload.AsMemory(offset, Math.Min(50_000, payload.Length - offset)));
            }
        });
        var received = new byte[payload.Length];
        await b.ReadExactlyAsync(received).AsTask().WithinAsync();
        await write.WithinAsync();

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task A_read_on_the_descriptor_stream_honours_cancellation_and_dispose()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The descriptor stream uses libc.");
        var (left, right) = SocketPair();
        await using var a = new VsockStream(left);
        await using var b = new VsockStream(right);
        using var cts = new CancellationTokenSource();

        var cancelled = a.ReadAsync(new byte[1], cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WithinAsync());

        var woken = b.ReadAsync(new byte[1]).AsTask();
        await b.DisposeAsync();
        Assert.Equal(0, await woken.WithinAsync());
    }

    [Fact]
    [Trait("Category", "Vsock")]
    public async Task Real_vsock_listener_binds_and_a_guest_connection_reaches_it_where_the_kernel_can_loop_back()
    {
        Assert.SkipUnless(VsockListener.IsSupported, "This machine has no AF_VSOCK transport.");
        var port = (uint)Random.Shared.Next(40000, 60000);
        await using var acceptor = new VsockAcceptor(new VsockListenerOptions(port));
        using var stop = new CancellationTokenSource();
        var accept = acceptor.AcceptAsync(stop.Token).AsTask();

        // CID 1 is the local loopback CID, which needs the vsock_loopback module. Without it the listener still bound, which is what we can check.
        VsockGuestTransport guest;
        try
        {
            guest = await VsockGuestTransport.ConnectAsync(1, port, TimeSpan.FromSeconds(3), TextWriter.Null, CancellationToken.None);
        }
        catch (IOException)
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept);
            Assert.Skip("The listener bound, but this kernel has no vsock loopback to connect through.");
            return;
        }

        using (guest)
        {
            var peer = await accept.WithinAsync();
            await using var host = peer.Stream;
            await guest.ToHost.WriteAsync(new byte[] { 5 });
            var read = new byte[1];
            await host.ReadExactlyAsync(read).AsTask().WithinAsync();

            Assert.Equal([5], read);
        }
    }

    [Fact]
    [Trait("Category", "HvSocket")]
    public async Task Real_hyperv_listener_binds_and_waits_on_a_windows_host()
    {
        Assert.SkipUnless(HvSocketListener.IsSupported, "Hyper-V sockets need a Windows host.");
        await using var listener = new HvSocketListener(new HvSocketListenerOptions(Guid.NewGuid()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => listener.AcceptAsync("6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60", cts.Token).AsTask());
    }

    private static (LinuxVsock.FdHandle Left, LinuxVsock.FdHandle Right) SocketPair()
    {
        Span<int> fds = stackalloc int[2];
        // AF_UNIX and SOCK_STREAM: a connected pair behaves like a connected vsock for the stream layer.
        if (socketpair(1, 1, 0, fds) != 0)
        {
            throw new IOException("socketpair failed");
        }

        return (LinuxVsock.Wrap(fds[0]), LinuxVsock.Wrap(fds[1]));
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socketpair(int domain, int type, int protocol, Span<int> fds);
}
