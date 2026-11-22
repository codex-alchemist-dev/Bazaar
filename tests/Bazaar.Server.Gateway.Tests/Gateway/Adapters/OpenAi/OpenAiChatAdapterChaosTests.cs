using System.Diagnostics;
using System.Net;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;
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

public class OpenAiChatAdapterUsageNumbersTests
{
    private static string Stream(string usageJson) =>
        "data: {\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: {\"model\":\"m\",\"choices\":[],\"usage\":" + usageJson + "}\n\n"
        + "data: [DONE]\n\n";

    private static async Task<List<ModelEvent>> Events(string usageJson)
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Sse(Stream(usageJson)));
        return await Run(handler);
    }

    [Fact]
    public async Task A_count_past_the_long_range_is_read_exactly()
    {
        var events = await Events("""{"prompt_tokens":92233720368547758070,"completion_tokens":3,"prompt_tokens_details":{"cached_tokens":70}}""");

        var usage = Assert.Single(events.OfType<ModelEvent.UsageReported>()).Usage;
        Assert.Equal(TokenAmount.Parse("92233720368547758000"), usage.Input);
        Assert.Equal(TokenAmount.FromInt64(70), usage.CacheRead);
        Assert.Equal(TokenAmount.FromInt64(3), usage.Output);
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task A_numeric_string_count_is_read_and_a_decimal_is_exact()
    {
        var usage = Assert.Single((await Events("""{"prompt_tokens":"40","completion_tokens":2.5e0}""")).OfType<ModelEvent.UsageReported>()).Usage;

        Assert.Equal(TokenAmount.FromInt64(40), usage.Input);
        Assert.Equal(TokenAmount.Parse("2.5"), usage.Output);
    }

    [Theory]
    [InlineData("""{"prompt_tokens":-5,"completion_tokens":3}""")]
    [InlineData("""{"prompt_tokens":5,"completion_tokens":-9223372036854775809}""")]
    [InlineData("""{"prompt_tokens":5,"completion_tokens":"lots"}""")]
    [InlineData("""{"prompt_tokens":5,"completion_tokens":true}""")]
    [InlineData("""{"prompt_tokens":1e999999999,"completion_tokens":3}""")]
    public async Task A_negative_or_malformed_count_drops_the_report_and_the_call_still_completes(string usageJson)
    {
        var events = await Events(usageJson);

        Assert.DoesNotContain(events, e => e is ModelEvent.UsageReported);
        Assert.DoesNotContain(events, e => e is ModelEvent.Failure);
        Assert.IsType<ModelEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Cached_above_prompt_leaves_no_uncached_input()
    {
        var usage = Assert.Single((await Events("""{"prompt_tokens":5,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":9}}""")).OfType<ModelEvent.UsageReported>()).Usage;

        Assert.Equal(TokenAmount.Zero, usage.Input);
        Assert.Equal(TokenAmount.FromInt64(9), usage.CacheRead);
    }
}
