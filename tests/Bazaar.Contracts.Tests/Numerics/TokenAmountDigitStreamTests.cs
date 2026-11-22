using System.Text;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Contracts.Tests.Numerics;

/// <summary>
/// The digit-stream intake of the chunk codec (doc 48, DECIDED 11.4, "Transport above the frame size"): a plain decimal read
/// from a stream of unknown length is written in the same canonical chunked form as <see cref="TokenAmountChunkCodec.WriteDecimalAsync"/>,
/// the intake guard stops the read at the first digit beyond it, and buffer use stays a few chunks whatever the length.
/// </summary>
public class TokenAmountDigitStreamTests
{
    private const int _seed = 20261119;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TokenAmountChunkCodec Codec(int chunkBytes, TokenAmountBufferPool? pool = null) =>
        new(new TokenAmountChunkLayout(chunkBytes), pool ?? new TokenAmountBufferPool());

    public static TheoryData<int> ChunkSizes() => [1, 2, 3, 4, 7];

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task A_stream_is_written_exactly_as_its_canonical_text_across_many_chunks(int chunkBytes)
    {
        var random = new Random(_seed + chunkBytes);
        var codec = Codec(chunkBytes);
        for (var round = 0; round < 60; round++)
        {
            // Leading integer zeros and trailing fraction zeros, which the stream drops, around random digits.
            var integer = new string('0', random.Next(0, 5)) + Digits(random, random.Next(0, 40));
            var fraction = random.Next(3) == 0 ? null : Digits(random, random.Next(1, 40)) + new string('0', random.Next(0, 5));
            if (integer.Length == 0)
            {
                integer = "0";
            }

            var text = fraction is null ? integer : integer + "." + fraction;

            var streamed = new InMemoryChunkedTokenAmount();
            var digits = await codec.WriteDecimalStreamAsync(
                new TrickleStream(Encoding.ASCII.GetBytes(text), random.Next(1, 5)), TokenAmountBounds.Unlimited,
                new InMemoryTokenAmountDigitScratch(codec.Pool), streamed, Ct);

            var expected = new InMemoryChunkedTokenAmount();
            await codec.WriteDecimalAsync(text.AsMemory(), expected, Ct);

            Assert.Equal(expected.Digits, digits);
            Assert.Equal(expected.Digits, streamed.Digits);
            Assert.Equal(expected.ChunkCount, streamed.ChunkCount);
            Assert.Equal(expected.Sha256.ToArray(), streamed.Sha256.ToArray());
            Assert.Equal(TokenAmount.Parse(text, TokenAmountBounds.Unlimited), await codec.TryReadAsync(streamed, long.MaxValue, Ct));
        }
    }

    [Fact]
    public async Task Zero_written_with_zeros_on_both_sides_has_no_chunks()
    {
        var codec = Codec(2);
        var streamed = new InMemoryChunkedTokenAmount();

        var digits = await codec.WriteDecimalStreamAsync(Ascii("0000.000"), TokenAmountBounds.Unlimited, new InMemoryTokenAmountDigitScratch(codec.Pool), streamed, Ct);

        Assert.True(digits.IsZero);
        Assert.Equal(0, streamed.ChunkCount);
        Assert.Equal(TokenAmountChunkCodec.ZeroSha256.ToArray(), streamed.Sha256.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1.2.3")]
    [InlineData("12 34")]
    [InlineData("1234\n")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("1e5")]
    [InlineData("12x34")]
    public async Task Text_that_is_not_a_plain_decimal_is_refused_and_nothing_is_completed(string text)
    {
        var codec = Codec(2);
        var destination = new InMemoryChunkedTokenAmount();

        var ex = await Assert.ThrowsAsync<TokenAmountIntakeException>(async () =>
            await codec.WriteDecimalStreamAsync(Ascii(text), TokenAmountBounds.Unlimited, new InMemoryTokenAmountDigitScratch(codec.Pool), destination, Ct));

        Assert.Equal(TokenAmountIntakeFailure.Malformed, ex.Failure);
        Assert.False(destination.IsComplete);
        Assert.Equal(0, destination.ChunkCount);
    }

    [Fact]
    public async Task A_non_digit_byte_mid_stream_stops_the_read_there()
    {
        var codec = Codec(2);
        var body = Encoding.ASCII.GetBytes(new string('7', 5000) + "x" + new string('7', 5000));
        var source = new TrickleStream(body, 100);
        var scratch = new InMemoryTokenAmountDigitScratch(codec.Pool);

        var ex = await Assert.ThrowsAsync<TokenAmountIntakeException>(async () =>
            await codec.WriteDecimalStreamAsync(source, TokenAmountBounds.Unlimited, scratch, new InMemoryChunkedTokenAmount(), Ct));

        Assert.Equal(TokenAmountIntakeFailure.Malformed, ex.Failure);
        Assert.Contains("byte 5001", ex.Message, StringComparison.Ordinal);
        Assert.True(source.Position < body.Length, "The read continued past the bad byte.");
        Assert.Equal(0, codec.Pool.Outstanding);
    }

    [Theory]
    [InlineData("123456", 5, 10, "before")]
    [InlineData("000001", 5, 10, "before")]
    [InlineData("1.123456", 5, 5, "after")]
    [InlineData("1.100000", 5, 5, "after")]
    public async Task The_guard_counts_digits_as_sent_and_stops_at_the_first_digit_beyond_it(string text, int maxInteger, int maxFraction, string side)
    {
        var codec = Codec(2);
        var destination = new InMemoryChunkedTokenAmount();

        var ex = await Assert.ThrowsAsync<TokenAmountIntakeException>(async () =>
            await codec.WriteDecimalStreamAsync(
                Ascii(text), new TokenAmountBounds(maxInteger, maxFraction), new InMemoryTokenAmountDigitScratch(codec.Pool), destination, Ct));

        Assert.Equal(TokenAmountIntakeFailure.OverGuard, ex.Failure);
        Assert.Contains($"digits {side} the point", ex.Message, StringComparison.Ordinal);
        Assert.False(destination.IsComplete);
    }

    [Fact]
    public async Task The_guard_stops_a_long_stream_mid_way_without_reading_the_rest()
    {
        var codec = Codec(4);
        var body = Encoding.ASCII.GetBytes("1" + new string('0', 100_000));
        var source = new TrickleStream(body, 64);

        var ex = await Assert.ThrowsAsync<TokenAmountIntakeException>(async () =>
            await codec.WriteDecimalStreamAsync(
                source, new TokenAmountBounds(1_000, 10), new InMemoryTokenAmountDigitScratch(codec.Pool), new InMemoryChunkedTokenAmount(), Ct));

        Assert.Equal(TokenAmountIntakeFailure.OverGuard, ex.Failure);
        Assert.InRange(source.Position, 1_001, 1_001 + 64);
        Assert.Equal(0, codec.Pool.Outstanding);
    }

    [Fact]
    public async Task Text_exactly_at_the_guard_is_accepted()
    {
        var codec = Codec(3);
        var streamed = new InMemoryChunkedTokenAmount();

        var digits = await codec.WriteDecimalStreamAsync(
            Ascii("12345.123"), new TokenAmountBounds(5, 3), new InMemoryTokenAmountDigitScratch(codec.Pool), streamed, Ct);

        Assert.Equal(new TokenAmountDigitCounts(5, 3), digits);
        Assert.Equal(TokenAmount.Parse("12345.123"), await codec.TryReadAsync(streamed, 100, Ct));
    }

    [Fact]
    public async Task Buffer_use_stays_a_few_chunks_whatever_the_length()
    {
        var pool = new TokenAmountBufferPool();
        var codec = Codec(4, pool);
        var random = new Random(_seed);
        var text = "9" + Digits(random, 40_000) + "." + Digits(random, 9_999) + "3";
        var streamed = new InMemoryChunkedTokenAmount();
        var scratch = new InMemoryTokenAmountDigitScratch(pool);

        pool.ResetPeak();
        var digits = await codec.WriteDecimalStreamAsync(new TrickleStream(Encoding.ASCII.GetBytes(text), 1_000), TokenAmountBounds.Unlimited, scratch, streamed, Ct);

        // About 6,250 chunks were written, but at most the read buffer and three chunk buffers were held at once.
        Assert.Equal(new TokenAmountDigitCounts(40_001, 10_000), digits);
        Assert.True(streamed.ChunkCount > 6_000);
        Assert.InRange(pool.Peak, 1, 3);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(TokenAmount.Parse(text, TokenAmountBounds.Unlimited), await codec.TryReadAsync(streamed, long.MaxValue, Ct));
    }

    [Fact]
    public async Task Cancellation_mid_stream_stops_the_read_and_completes_nothing()
    {
        var codec = Codec(2);
        using var cts = new CancellationTokenSource();
        var source = new TrickleStream(Encoding.ASCII.GetBytes(new string('5', 10_000)), 10, afterReads: 50, onRead: cts.Cancel);
        var destination = new InMemoryChunkedTokenAmount();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await codec.WriteDecimalStreamAsync(source, TokenAmountBounds.Unlimited, new InMemoryTokenAmountDigitScratch(codec.Pool), destination, cts.Token));

        Assert.False(destination.IsComplete);
        Assert.Equal(0, codec.Pool.Outstanding);
    }

    private static string Digits(Random random, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('0' + random.Next(10));
        }

        return new string(chars);
    }

    private static MemoryStream Ascii(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>A read-only stream that returns at most a few bytes per read, as a network body does.</summary>
    private sealed class TrickleStream(byte[] data, int maxPerRead, int afterReads = int.MaxValue, Action? onRead = null) : Stream
    {
        private long _position;
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = (int)Math.Min(Math.Min(buffer.Length, maxPerRead), data.Length - _position);
            data.AsSpan((int)_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads >= afterReads)
            {
                onRead?.Invoke();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
