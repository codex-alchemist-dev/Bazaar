using System.Threading.Channels;
using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport.Tests.Adversarial;

/// <summary>Connections nobody expects, repeated or mislabelled, and ids written in odd forms.</summary>
public class HostileListenerTests
{
    private const string _vmA = "6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60";
    private const string _vmB = "0a0b0c0d-1111-2222-3333-444455556666";

    [Fact]
    public async Task A_connection_from_a_peer_nobody_waits_for_is_closed_at_once()
    {
        await using var listener = new LoopbackListener();
        var wait = listener.AcceptAsync("vm-a", CancellationToken.None).AsTask();

        var stranger = listener.Connect("vm-stranger");

        Assert.Equal(0, await stranger.FromHost.ReadAsync(new byte[1]).AsTask().WithinAsync());
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task A_second_connection_from_the_same_peer_after_the_waiter_was_served_is_closed()
    {
        await using var listener = new LoopbackListener();
        var wait = listener.AcceptAsync("vm-a", CancellationToken.None);
        listener.Connect("vm-a");
        await using var first = await wait.AsTask().WithinAsync();

        var second = listener.Connect("vm-a");

        Assert.Equal(0, await second.FromHost.ReadAsync(new byte[1]).AsTask().WithinAsync());
        // The served connection is untouched by the intruder's close.
        Assert.True(first.Stream.CanRead);
    }

    [Fact]
    public async Task A_peer_is_never_handed_to_a_waiter_for_another_id()
    {
        await using var listener = new LoopbackListener();
        var waitA = listener.AcceptAsync("vm-a", CancellationToken.None).AsTask();
        var waitB = listener.AcceptAsync("vm-b", CancellationToken.None);

        listener.Connect("vm-b");
        await using var connection = await waitB.AsTask().WithinAsync();

        Assert.Equal("vm-b", connection.PeerVmId);
        await Task.Delay(100);
        Assert.False(waitA.IsCompleted);
    }

    [Fact]
    public async Task Hyperv_peers_that_the_hypervisor_labels_as_another_vm_or_garbage_are_closed()
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new HvSocketListener(acceptor: acceptor);
        var wait = listener.AcceptAsync(_vmA, CancellationToken.None);

        var other = new TrackedStream();
        var garbage = new TrackedStream();
        var empty = new TrackedStream();
        acceptor.Offer(_vmB, other);
        acceptor.Offer("not-a-guid", garbage);
        acceptor.Offer("", empty);
        var real = new TrackedStream();
        acceptor.Offer(_vmA, real);
        await using var connection = await wait.AsTask().WithinAsync();

        Assert.True(other.Disposed);
        Assert.True(garbage.Disposed);
        Assert.True(empty.Disposed);
        Assert.Same(real, connection.Stream);
    }

    [Theory]
    [InlineData("6F1C3B7A-2D48-4F6E-9A35-1B2C3D4E5F60")]
    [InlineData("{6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60}")]
    [InlineData("6f1c3b7a2d484f6e9a351b2c3d4e5f60")]
    [InlineData("(6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60)")]
    public async Task Guid_forms_all_normalise_to_one_id(string written)
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new HvSocketListener(acceptor: acceptor);
        var wait = listener.AcceptAsync(written, CancellationToken.None);

        acceptor.Offer(_vmA, new TrackedStream());

        await using var connection = await wait.AsTask().WithinAsync();
        Assert.Equal(_vmA, connection.PeerVmId);
    }

    [Fact]
    public async Task A_second_connection_after_a_hyperv_waiter_was_served_is_closed()
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new HvSocketListener(acceptor: acceptor);
        var wait = listener.AcceptAsync(_vmA, CancellationToken.None);
        acceptor.Offer(_vmA, new TrackedStream());
        await using var served = await wait.AsTask().WithinAsync();

        var again = new TrackedStream();
        acceptor.Offer(_vmA, again);
        await Task.Delay(100);

        Assert.True(again.Disposed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("00002")]
    [InlineData("+3")]
    [InlineData(" 3")]
    [InlineData("3 ")]
    [InlineData("-3")]
    [InlineData("0x3")]
    [InlineData("3.0")]
    [InlineData("4294967296")]
    [InlineData("")]
    public async Task Vsock_ids_that_are_not_a_plain_guest_cid_are_refused(string written)
    {
        await using var listener = new VsockListener(acceptor: new FakeAcceptor());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => listener.AcceptAsync(written, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Vsock_ids_with_leading_zeros_normalise_and_other_cids_are_closed()
    {
        var acceptor = new FakeAcceptor();
        await using var listener = new VsockListener(acceptor: acceptor);
        var wait = listener.AcceptAsync("0003", CancellationToken.None);

        var host = new TrackedStream();
        var other = new TrackedStream();
        acceptor.Offer("2", host);
        acceptor.Offer("4", other);
        var real = new TrackedStream();
        acceptor.Offer("3", real);
        await using var connection = await wait.AsTask().WithinAsync();

        Assert.True(host.Disposed);
        Assert.True(other.Disposed);
        Assert.Equal("3", connection.PeerVmId);
        Assert.Same(real, connection.Stream);
    }

    private sealed class FakeAcceptor : IPeerAcceptor
    {
        private readonly Channel<AcceptedPeer> _peers = Channel.CreateUnbounded<AcceptedPeer>();

        public void Offer(string peerId, Stream stream) => _peers.Writer.TryWrite(new AcceptedPeer(peerId, stream));

        public ValueTask<AcceptedPeer> AcceptAsync(CancellationToken ct) => _peers.Reader.ReadAsync(ct);

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
