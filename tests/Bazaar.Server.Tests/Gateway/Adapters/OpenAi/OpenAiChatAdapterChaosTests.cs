using System.Diagnostics;
using System.Net;
using System.Text;
using Bazaar.Contracts.Gateway;
using static Bazaar.Server.Tests.Gateway.Adapters.OpenAi.OpenAiChatTestRig;

namespace Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): an upstream that sends the headers and one chunk, then never sends another byte.
/// Seen black-box on a running host: the agent responder's call stayed open for over 15 minutes and every later mention in
/// that GC queued behind it. The client's timeout must bound the whole streamed call.
/// </summary>
public class OpenAiChatAdapterChaosTests
{
    private const string _firstChunk = "data: {\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"thinking...\"}}]}\n\n";

    [Fact]
    public async Task A_stream_that_stalls_after_the_first_chunk_ends_as_a_timeout()
    {
        var handler = new StubHandler();
        handler.Reply(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(_firstChunk))),
        }));
        var clock = Stopwatch.StartNew();

        var events = await Run(handler, timeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal("thinking...", TextOf(events));
        var failure = Assert.IsType<ModelEvent.Failure>(events[^1]);
        Assert.Equal("timeout", failure.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The call took {clock.Elapsed}.");
    }

    [Fact]
    public async Task The_callers_own_cancel_still_propagates_during_a_stall()
    {
        var handler = new StubHandler();
        handler.Reply(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(_firstChunk))),
        }));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(handler, timeout: TimeSpan.FromMinutes(5), ct: cts.Token));
    }

    /// <summary>Serves its bytes, then blocks every read until it is cancelled, like a peer that stopped sending.</summary>
    private sealed class StallingStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Reads are async only.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
