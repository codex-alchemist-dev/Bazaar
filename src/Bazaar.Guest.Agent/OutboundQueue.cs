using System.Threading.Channels;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// The only writer of the guest channel. A frame, and any raw bytes that follow it, is queued as one item, and the
/// writer writes one item at a time. Frames from concurrent execs therefore never interleave, and a file body
/// follows its PutFile header directly. The queue is bounded, so a slow host slows the producers instead of growing
/// memory without limit.
/// </summary>
internal sealed class OutboundQueue
{
    /// <summary>Largest raw chunk put in one Output or PtyData frame. Base64 keeps that well under the frame cap.</summary>
    public const int MaxPayloadBytes = 32 * 1024;

    private const int _capacity = 256;

    private readonly Channel<Func<Stream, CancellationToken, Task>> _items =
        Channel.CreateBounded<Func<Stream, CancellationToken, Task>>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>Queues one frame.</summary>
    public ValueTask SendAsync(GuestMessage message, CancellationToken ct) =>
        EnqueueAsync((stream, token) => GuestFrameCodec.WriteAsync(stream, message, token), ct);

    /// <summary>Queues a write that may emit one frame followed by raw bytes. It runs as one unit.</summary>
    public ValueTask EnqueueAsync(Func<Stream, CancellationToken, Task> write, CancellationToken ct) =>
        _items.Writer.WriteAsync(write, ct);

    /// <summary>Stops accepting items. Items already queued are still written.</summary>
    public void Complete() => _items.Writer.TryComplete();

    /// <summary>Writes queued items to <paramref name="output"/> until the queue is completed.</summary>
    public async Task RunAsync(Stream output, CancellationToken ct)
    {
        try
        {
            await foreach (var write in _items.Reader.ReadAllAsync(ct))
            {
                await write(output, ct);
            }
        }
        catch (Exception ex)
        {
            // Producers still waiting on a queue nobody drains now see ChannelClosedException instead of hanging.
            _items.Writer.TryComplete(ex);
            throw;
        }
    }
}
