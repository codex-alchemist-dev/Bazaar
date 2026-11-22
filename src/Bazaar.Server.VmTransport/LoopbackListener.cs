using System.IO.Pipelines;
using System.Threading.Channels;

namespace Bazaar.Server.VmTransport;

/// <summary>The guest's two directions of a loopback connection, shaped like the guest agent's transport streams.</summary>
/// <param name="FromHost">Bytes the host sent.</param>
/// <param name="ToHost">Bytes for the host.</param>
public sealed record LoopbackGuestEnd(Stream FromHost, Stream ToHost);

/// <summary>
/// An in-memory listener for tests and for hosts that run the guest agent in-process. <see cref="Connect"/> plays a guest dialling in: it hands
/// the host side to whoever waits for that peer id and returns the guest's streams.
/// </summary>
public sealed class LoopbackListener : PeerMatchedListener
{
    private readonly LoopbackAcceptor _acceptor;

    /// <summary>Creates a listener.</summary>
    public LoopbackListener()
        : this(new LoopbackAcceptor())
    {
    }

    private LoopbackListener(LoopbackAcceptor acceptor)
        : base(GuestTransportKind.Loopback, () => acceptor)
    {
        _acceptor = acceptor;
    }

    /// <summary>A guest with peer id <paramref name="peerVmId"/> dials in. It is dropped if the host is not waiting for that id.</summary>
    public LoopbackGuestEnd Connect(string peerVmId)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerVmId);
        var toGuest = new Pipe();
        var toHost = new Pipe();
        // Disposing the host stream completes its writer and reader, so the guest reads end of stream, as it would after a socket closes.
        var hostStream = new DuplexStream(toHost.Reader.AsStream(), toGuest.Writer.AsStream());
        _acceptor.Offer(new AcceptedPeer(peerVmId, hostStream));
        return new LoopbackGuestEnd(toGuest.Reader.AsStream(), toHost.Writer.AsStream());
    }

    /// <inheritdoc />
    protected override string Normalize(string vmId)
    {
        ArgumentException.ThrowIfNullOrEmpty(vmId);
        return vmId;
    }

    private sealed class LoopbackAcceptor : IPeerAcceptor
    {
        private readonly Channel<AcceptedPeer> _peers = Channel.CreateUnbounded<AcceptedPeer>();

        public void Offer(AcceptedPeer peer) => _peers.Writer.TryWrite(peer);

        public ValueTask<AcceptedPeer> AcceptAsync(CancellationToken ct) => _peers.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync()
        {
            _peers.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Joins a read stream and a write stream into one duplex stream.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly Stream _read;
    private readonly Stream _write;
    private readonly Action? _onDispose;
    private int _disposed;

    /// <summary>Creates the stream. Disposing it disposes both halves, then runs <paramref name="onDispose"/>.</summary>
    public DuplexStream(Stream read, Stream write, Action? onDispose = null)
    {
        _read = read;
        _write = write;
        _onDispose = onDispose;
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _read.ReadAsync(buffer, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _write.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _write.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _read.Dispose();
            _write.Dispose();
            _onDispose?.Invoke();
        }

        base.Dispose(disposing);
    }
}
