using System.Threading.Channels;
using Bazaar.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>Listener matching, driven by fake peers because real Hyper-V and vsock peers cannot exist in the cloud build.</summary>
public class ListenerTests
{
    private const string _vmA = "6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60";
    private const string _vmB = "0a0b0c0d-1111-2222-3333-444455556666";

    [Fact]
    public async Task Loopback_hands_a_connection_to_the_waiter_for_that_peer()
    {
        await using var listener = new LoopbackListener();
        var wait = listener.AcceptAsync("vm-a", CancellationToken.None);

        var end = listener.Connect("vm-a");
        await using var connection = await wait.AsTask().WithinAsync();

        Assert.Equal("vm-a", connection.PeerVmId);
        Assert.Equal(GuestTransportKind.Loopback, connection.Kind);
        await end.ToHost.WriteAsync(new byte[] { 42 });
        var read = new byte[1];
        await connection.Stream.ReadExactlyAsync(read).AsTask().WithinAsync();
        Assert.Equal(42, read[0]);
    }

    [Fact]
    public async Task A_peer_nobody_waits_for_is_closed_and_the_right_peer_still_gets_through()
    {
        await using var listener = new LoopbackListener();
        var wait = listener.AcceptAsync("vm-a", CancellationToken.None);

        var intruder = listener.Connect("vm-b");
        var eof = await intruder.FromHost.ReadAsync(new byte[1]).AsTask().WithinAsync();
        var real = listener.Connect("vm-a");
        await using var connection = await wait.AsTask().WithinAsync();

        Assert.Equal(0, eof);
        Assert.Equal("vm-a", connection.PeerVmId);
        Assert.NotNull(real);
    }

    [Fact]
    public async Task A_second_wait_for_the_same_vm_replaces_the_first()
    {
        await using var listener = new LoopbackListener();
        var first = listener.AcceptAsync("vm-a", CancellationToken.None).AsTask();
        var second = listener.AcceptAsync("vm-a", CancellationToken.None).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WithinAsync());
        listener.Connect("vm-a");
        await using var connection = await second.WithinAsync();
        Assert.Equal("vm-a", connection.PeerVmId);
    }

    [Fact]
    public async Task A_cancelled_wait_stops_waiting_and_a_later_connection_is_refused()
    {
        await using var listener = new LoopbackListener();
        using var cts = new CancellationTokenSource();
        var wait = listener.AcceptAsync("vm-a", cts.Token).AsTask();

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WithinAsync());
        var late = listener.Connect("vm-a");

        Assert.Equal(0, await late.FromHost.ReadAsync(new byte[1]).AsTask().WithinAsync());
    }

    [Fact]
    public async Task Disposing_the_listener_cancels_waiters()
    {
        var listener = new LoopbackListener();
        var wait = listener.AcceptAsync("vm-a", CancellationToken.None).AsTask();

        await listener.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WithinAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => listener.AcceptAsync("vm-a", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task HvSocket_listener_rejects_a_peer_whose_hypervisor_reported_id_is_another_vm()
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new HvSocketListener(acceptor: acceptor);
        var wait = listener.AcceptAsync(_vmA, CancellationToken.None);

        var intruder = new TrackedStream();
        acceptor.Offer(_vmB, intruder);
        var real = new TrackedStream();
        acceptor.Offer(_vmA, real);
        await using var connection = await wait.AsTask().WithinAsync();

        Assert.True(intruder.Disposed);
        Assert.Same(real, connection.Stream);
        Assert.Equal(_vmA, connection.PeerVmId);
        Assert.Equal(GuestTransportKind.HvSocket, connection.Kind);
    }

    [Fact]
    public async Task HvSocket_listener_matches_a_guid_written_in_any_case_or_braces()
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new HvSocketListener(acceptor: acceptor);
        var wait = listener.AcceptAsync("{" + _vmA.ToUpperInvariant() + "}", CancellationToken.None);

        acceptor.Offer(_vmA, new TrackedStream());

        await using var connection = await wait.AsTask().WithinAsync();
        Assert.Equal(_vmA, connection.PeerVmId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task HvSocket_listener_refuses_a_vm_id_that_is_not_a_vm_guid(string vmId)
    {
        await using var listener = new HvSocketListener(acceptor: new FakeAcceptor());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => listener.AcceptAsync(vmId, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Vsock_listener_matches_the_peer_cid_and_rejects_others()
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new VsockListener(acceptor: acceptor);
        var wait = listener.AcceptAsync("42", CancellationToken.None);

        var intruder = new TrackedStream();
        acceptor.Offer("43", intruder);
        acceptor.Offer("42", new TrackedStream());
        await using var connection = await wait.AsTask().WithinAsync();

        Assert.True(intruder.Disposed);
        Assert.Equal("42", connection.PeerVmId);
        Assert.Equal(GuestTransportKind.Vsock, connection.Kind);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("4294967296")]
    public async Task Vsock_listener_refuses_a_value_that_is_not_a_guest_cid(string vmId)
    {
        await using var listener = new VsockListener(acceptor: new FakeAcceptor());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => listener.AcceptAsync(vmId, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task A_listener_rebuilds_its_socket_layer_after_an_io_failure()
    {
        var acceptor = new FakeAcceptor { FailNext = true };
        await using var listener = new VsockListener(acceptor: acceptor);
        var wait = listener.AcceptAsync("42", CancellationToken.None);

        acceptor.Offer("42", new TrackedStream());

        await using var connection = await wait.AsTask().WithinAsync();
        Assert.Equal("42", connection.PeerVmId);
    }

    [Fact]
    public async Task A_factory_opens_a_channel_from_a_waiting_listener_and_checks_the_hello()
    {
        await using var listener = new LoopbackListener();
        var factory = new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System);
        var open = factory.OpenAsync(listener, "vm-a", new GuestHandshake("vm-x", "n-1"), CancellationToken.None);

        var end = listener.Connect("vm-a");
        await GuestFrameCodec.WriteAsync(end.ToHost, new Hello("v", "vm-x", "n-1"), CancellationToken.None);

        await using var channel = await open.WithinAsync();
        Assert.Equal(GuestChannelState.Open, channel.State);
    }

    [Fact]
    public void Registration_adds_the_factory_and_does_not_replace_a_listener_the_host_chose()
    {
        var chosen = new LoopbackListener();
        var services = new ServiceCollection();
        services.AddSingleton<IGuestConnectionListener>(chosen);

        services.AddBazaarVmTransport();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<GuestChannelFactory>());
        Assert.Same(chosen, provider.GetRequiredService<IGuestConnectionListener>());
        Assert.NotNull(provider.GetRequiredService<GuestChannelOptions>());
    }

    [Fact]
    public void The_module_declares_windows_and_linux_hosts()
    {
        Assert.Equal(["windows", "linux"], VmTransportModule.Requirements.OperatingSystems);
        Assert.Empty(Contracts.Modules.HostCompatibility.ValidateRequirements(VmTransportModule.Requirements));


    }

    /// <summary>A socket layer the test drives: it yields the peers the test offers, with whatever id the test says the hypervisor reported.</summary>
    private sealed class FakeAcceptor : IPeerAcceptor
    {
        private readonly Channel<AcceptedPeer> _peers = Channel.CreateUnbounded<AcceptedPeer>();

        public bool FailNext { get; set; }

        public void Offer(string peerId, Stream stream) => _peers.Writer.TryWrite(new AcceptedPeer(peerId, stream));

        public ValueTask<AcceptedPeer> AcceptAsync(CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("the socket layer broke");
            }

            return _peers.Reader.ReadAsync(ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackedStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
