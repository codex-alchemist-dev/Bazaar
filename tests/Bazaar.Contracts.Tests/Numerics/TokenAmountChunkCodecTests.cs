using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Contracts.Tests.Numerics;

/// <summary>
/// The chunked form of large token amounts (doc 48, DECIDED 11.4): packing, canonical form, hash order, comparison and the
/// streamed addition and subtraction. Property tests run against <see cref="BigInteger"/> with a fixed seed and small chunk
/// sizes, so carries, borrows and pads cross many chunk boundaries with short numbers.
/// </summary>
public class TokenAmountChunkCodecTests
{
    private const int _seed = 20261119;

    private static readonly int[] _chunkSizes = [1, 2, 3, 4, 7];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TokenAmountChunkCodec Codec(int chunkBytes, TokenAmountBufferPool? pool = null) =>
        new(new TokenAmountChunkLayout(chunkBytes), pool ?? new TokenAmountBufferPool());

    public static TheoryData<int> ChunkSizes() => [.. _chunkSizes];

    [Fact]
    public void Layout_counts_bytes_and_chunks_with_one_pad_digit()
    {
        var layout = new TokenAmountChunkLayout(3);

        Assert.Equal(0, TokenAmountChunkLayout.PackedBytes(0));
        Assert.Equal(1, TokenAmountChunkLayout.PackedBytes(1));
        Assert.Equal(1, TokenAmountChunkLayout.PackedBytes(2));
        Assert.Equal(4, TokenAmountChunkLayout.PackedBytes(7));
        Assert.Equal(0, layout.ChunkCount(0));
        Assert.Equal(2, layout.ChunkCount(7));
        Assert.Equal(3, layout.ChunkLength(7, 0));
        Assert.Equal(1, layout.ChunkLength(7, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ChunkLength(7, 2));
        Assert.Equal(1 << 20, TokenAmountChunkLayout.Standard.ChunkBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenAmountChunkLayout(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenAmountChunkLayout((1 << 20) + 1));
    }

    [Fact]
    public async Task Packing_is_base_100_aligned_at_the_point_with_numbered_chunks()
    {
        // 12345.6789 with one byte per chunk: integer 01|23|45 numbered from the point outward, fraction 67|89 onward.
        var amount = await Write(Codec(1), "12345.6789");

        Assert.Equal(new TokenAmountDigitCounts(5, 4), amount.Digits);
        Assert.Equal([45], await Chunk(amount, TokenAmountPart.IntegerPart, 0));
        Assert.Equal([23], await Chunk(amount, TokenAmountPart.IntegerPart, 1));
        Assert.Equal([1], await Chunk(amount, TokenAmountPart.IntegerPart, 2));
        Assert.Equal([67], await Chunk(amount, TokenAmountPart.FractionPart, 0));
        Assert.Equal([89], await Chunk(amount, TokenAmountPart.FractionPart, 1));
        Assert.Equal(5, amount.ChunkCount);
    }

    [Fact]
    public async Task Chunks_are_in_reading_order_and_only_the_outermost_is_short()
    {
        // Integer 1234567 -> bytes 01 23 45 67; with 3-byte chunks seq 0 is 23 45 67 and seq 1 (outermost) is 01.
        // Fraction 1234567 -> bytes 12 34 56 70; seq 0 is 12 34 56 and seq 1 (outermost) is 70 (trailing pad digit).
        var amount = await Write(Codec(3), "1234567.1234567");

        Assert.Equal([23, 45, 67], await Chunk(amount, TokenAmountPart.IntegerPart, 0));
        Assert.Equal([1], await Chunk(amount, TokenAmountPart.IntegerPart, 1));
        Assert.Equal([12, 34, 56], await Chunk(amount, TokenAmountPart.FractionPart, 0));
        Assert.Equal([70], await Chunk(amount, TokenAmountPart.FractionPart, 1));
    }

    [Theory]
    [InlineData("000123.4500", "123.45", 3, 2)]
    [InlineData("0.000", "0", 0, 0)]
    [InlineData("0", "0", 0, 0)]
    [InlineData("0.5", "0.5", 0, 1)]
    [InlineData("10", "10", 2, 0)]
    [InlineData("0001", "1", 1, 0)]
    public async Task Writing_is_canonical_and_counts_exclude_pads(string text, string canonical, long integerDigits, long fractionDigits)
    {
        foreach (var size in _chunkSizes)
        {
            var codec = Codec(size);
            var amount = await Write(codec, text);

            Assert.Equal(new TokenAmountDigitCounts(integerDigits, fractionDigits), amount.Digits);
            Assert.Equal(TokenAmount.Parse(canonical), await codec.TryReadAsync(amount, long.MaxValue, Ct));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1e5")]
    [InlineData("-1")]
    [InlineData("1,000")]
    [InlineData(" 1")]
    [InlineData("1.2.3")]
    [InlineData("١")]
    public async Task Writing_refuses_anything_but_a_plain_decimal(string text)
    {
        var sink = new InMemoryChunkedTokenAmount();

        await Assert.ThrowsAsync<FormatException>(async () => await Codec(2).WriteDecimalAsync(text.AsMemory(), sink, Ct));
        Assert.Equal(0, sink.ChunkCount);
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task Round_trip_through_chunks_preserves_every_amount(int chunkBytes)
    {
        var codec = Codec(chunkBytes);
        var random = new Random(_seed + chunkBytes);
        for (var i = 0; i < 400; i++)
        {
            var text = RandomDecimal(random, 30);
            var amount = await Write(codec, text);

            var back = await codec.TryReadAsync(amount, long.MaxValue, Ct);

            Assert.Equal(TokenAmount.Parse(text), back);
            Assert.Equal(TokenAmountDigitCounts.Of(back!.Value), amount.Digits);
        }
    }

    [Fact]
    public async Task Conversion_from_TokenAmount_matches_the_text_form()
    {
        var codec = Codec(4);
        var value = TokenAmount.Parse("98765432109876543210.0123456789");

        var fromAmount = await InMemoryChunkedTokenAmount.FromAsync(value, codec, Ct);
        var fromText = await Write(codec, value.ToString());

        Assert.Equal(fromText.Sha256.ToArray(), fromAmount.Sha256.ToArray());
        Assert.Equal(value, await codec.TryReadAsync(fromAmount, long.MaxValue, Ct));
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task Addition_matches_BigInteger(int chunkBytes)
    {
        var codec = Codec(chunkBytes);
        var random = new Random(_seed * 3 + chunkBytes);
        for (var i = 0; i < 500; i++)
        {
            var left = RandomDecimal(random, 40);
            var right = RandomDecimal(random, 40);

            var sum = await Add(codec, left, right);

            Assert.Equal(Reference(left) + Reference(right), await Reference(codec, sum));
            await AssertCanonical(codec, sum);
        }
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task Subtraction_matches_BigInteger_and_never_goes_negative(int chunkBytes)
    {
        var codec = Codec(chunkBytes);
        var random = new Random(_seed * 5 + chunkBytes);
        for (var i = 0; i < 500; i++)
        {
            var left = RandomDecimal(random, 40);
            var right = random.Next(4) == 0 ? left : RandomDecimal(random, 40);
            var a = await Write(codec, left);
            var b = await Write(codec, right);
            var difference = new InMemoryChunkedTokenAmount();

            var done = await codec.TrySubtractAsync(a, b, difference, Ct);

            var expected = Reference(left) - Reference(right);
            if (expected < 0)
            {
                Assert.False(done);
                Assert.False(difference.IsComplete);
                Assert.Equal(0, difference.ChunkCount);
            }
            else
            {
                Assert.True(done);
                Assert.Equal(expected, await Reference(codec, difference));
                await AssertCanonical(codec, difference);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task Comparison_matches_BigInteger(int chunkBytes)
    {
        var codec = Codec(chunkBytes);
        var random = new Random(_seed * 7 + chunkBytes);
        for (var i = 0; i < 500; i++)
        {
            var left = RandomDecimal(random, 25);
            var right = random.Next(5) == 0 ? left : random.Next(2) == 0 ? Perturb(random, left) : RandomDecimal(random, 25);

            var order = await codec.CompareAsync(await Write(codec, left), await Write(codec, right), Ct);

            Assert.Equal(Reference(left).CompareTo(Reference(right)), order);
        }
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task An_all_nine_carry_ripples_across_every_chunk_and_grows_the_result(int chunkBytes)
    {
        var codec = Codec(chunkBytes);
        foreach (var length in new[] { 1, 2, 13, 14, 60, 61 })
        {
            var nines = new string('9', length);

            var integer = await Add(codec, nines, "1");
            var fraction = await Add(codec, "0." + nines, "0." + new string('0', length - 1) + "1");
            var both = await Add(codec, nines + "." + nines, "0." + new string('0', length - 1) + "1");

            Assert.Equal(new TokenAmountDigitCounts(length + 1, 0), integer.Digits);
            Assert.Equal(TokenAmount.Parse("1" + new string('0', length)), await codec.TryReadAsync(integer, long.MaxValue, Ct));
            Assert.Equal(TokenAmount.One, await codec.TryReadAsync(fraction, long.MaxValue, Ct));
            Assert.Equal(TokenAmount.Parse("1" + new string('0', length)), await codec.TryReadAsync(both, long.MaxValue, Ct));
        }
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public async Task A_borrow_chain_shrinks_the_result_across_many_chunks(int chunkBytes)
    {
        var codec = Codec(chunkBytes);
        foreach (var length in new[] { 2, 9, 30, 31 })
        {
            var power = "1" + new string('0', length);

            var one = await Subtract(codec, power, new string('9', length));
            var tail = await Subtract(codec, "1" + new string('0', length - 1) + "7", power);
            var fraction = await Subtract(codec, "1", "0." + new string('9', length));
            var zero = await Subtract(codec, power + ".25", power + ".25");

            Assert.Equal(TokenAmount.One, await codec.TryReadAsync(one, long.MaxValue, Ct));
            Assert.Equal(new TokenAmountDigitCounts(1, 0), one.Digits);
            Assert.Equal(TokenAmount.FromInt64(7), await codec.TryReadAsync(tail, long.MaxValue, Ct));
            Assert.Equal(new TokenAmountDigitCounts(0, length), fraction.Digits);
            Assert.Equal(new TokenAmountDigitCounts(0, 0), zero.Digits);
            Assert.Equal(0, zero.ChunkCount);
            Assert.Equal(TokenAmountChunkCodec.ZeroSha256.ToArray(), zero.Sha256.ToArray());
        }
    }

    [Fact]
    public async Task Comparison_reads_no_chunk_when_the_integer_digit_counts_differ()
    {
        var codec = Codec(2);
        var smaller = new CountingReader(await Write(codec, "99999999.99999999"));
        var larger = new CountingReader(await Write(codec, "100000000"));

        Assert.True(await codec.CompareAsync(smaller, larger, Ct) < 0);
        Assert.True(await codec.CompareAsync(larger, smaller, Ct) > 0);
        Assert.Equal(0, smaller.Reads);
        Assert.Equal(0, larger.Reads);
    }

    [Fact]
    public async Task Comparison_stops_at_the_first_difference_from_the_most_significant_end()
    {
        var codec = Codec(1);
        var left = new CountingReader(await Write(codec, "19" + new string('0', 40)));
        var right = new CountingReader(await Write(codec, "18" + new string('0', 40)));

        Assert.True(await codec.CompareAsync(left, right, Ct) > 0);
        Assert.Equal(1, left.Reads);
        Assert.Equal(1, right.Reads);
    }

    [Fact]
    public async Task Materialising_is_refused_above_the_given_maximum_without_reading()
    {
        var codec = Codec(3);
        var amount = new CountingReader(await Write(codec, "123456.789"));

        Assert.Null(await codec.TryReadAsync(amount, 8, Ct));
        Assert.Equal(0, amount.Reads);
        Assert.Equal(TokenAmount.Parse("123456.789"), await codec.TryReadAsync(amount, 9, Ct));
    }

    [Fact]
    public async Task The_hash_covers_fraction_chunks_outermost_first_then_integer_chunks_from_the_point()
    {
        // Standard layout, one chunk per part: the hash input is the fraction bytes, then the integer bytes, as stored.
        var single = await Write(TokenAmountChunkCodec.Standard, "123.45");
        Assert.Equal(SHA256.HashData(new byte[] { 45, 1, 23 }), single.Sha256.ToArray());

        // One-byte chunks: fraction seq 1 (89), seq 0 (67), then integer seq 0 (45), 1 (23), 2 (01).
        var split = await Write(Codec(1), "12345.6789");
        Assert.Equal(SHA256.HashData(new byte[] { 89, 67, 45, 23, 1 }), split.Sha256.ToArray());

        // Stable: the same amount always hashes the same, however it was produced.
        var codec = Codec(1);
        var sum = await Add(codec, "12345", "0.6789");
        Assert.Equal(split.Sha256.ToArray(), sum.Sha256.ToArray());
    }

    [Fact]
    public async Task A_tampered_byte_fails_the_hash_on_a_full_read()
    {
        var codec = Codec(2);
        var amount = await Write(codec, "123456789.123456789");
        var tampered = new TamperingReader(amount, TokenAmountPart.IntegerPart, 1, chunk => chunk[0] = (byte)((chunk[0] + 1) % 100));

        await Assert.ThrowsAsync<TokenAmountIntegrityException>(async () => await codec.TryReadAsync(tampered, long.MaxValue, Ct));
        await Assert.ThrowsAsync<TokenAmountIntegrityException>(async () => await codec.VerifyAsync(tampered, Ct));
        await Assert.ThrowsAsync<TokenAmountIntegrityException>(
            async () => await codec.AddAsync(tampered, amount, new InMemoryChunkedTokenAmount(), Ct));
    }

    [Fact]
    public async Task Malformed_chunks_are_integrity_failures()
    {
        var codec = Codec(2);
        var amount = await Write(codec, "12345.678");

        // A byte above 99, a short inner chunk, a leading zero byte and a missing pad all break the layout.
        await AssertMalformed(new TamperingReader(amount, TokenAmountPart.IntegerPart, 0, chunk => chunk[0] = 100));
        await AssertMalformed(new TamperingReader(amount, TokenAmountPart.IntegerPart, 0, null, truncate: true));
        await AssertMalformed(new TamperingReader(amount, TokenAmountPart.IntegerPart, 1, chunk => chunk[0] = 0));
        await AssertMalformed(new TamperingReader(amount, TokenAmountPart.FractionPart, 0, chunk => chunk[1] = 81));
        await AssertMalformed(new MissingChunkReader(amount));

        async Task AssertMalformed(ITokenAmountChunkReader reader) =>
            await Assert.ThrowsAsync<TokenAmountIntegrityException>(async () => await codec.VerifyAsync(reader, Ct));
    }

    [Fact]
    public async Task Streamed_arithmetic_holds_a_small_constant_number_of_chunks()
    {
        var pool = new TokenAmountBufferPool();
        var codec = Codec(3, pool);
        var random = new Random(_seed);
        var left = new PooledReader(await Write(codec, RandomDigits(random, 400) + "." + RandomDigits(random, 200)), pool);
        var right = new PooledReader(await Write(codec, RandomDigits(random, 399) + "." + RandomDigits(random, 250)), pool);
        pool.ResetPeak();

        var sum = new InMemoryChunkedTokenAmount();
        await codec.AddAsync(left, right, sum, Ct);
        var difference = new InMemoryChunkedTokenAmount();
        await codec.TrySubtractAsync(left, right, difference, Ct);
        await codec.CompareAsync(left, right, Ct);

        // One chunk per operand, plus the fill and held output chunks: four, for amounts of over a hundred chunks each.
        Assert.True(sum.ChunkCount > 100);
        Assert.InRange(pool.Peak, 1, 4);
        Assert.Equal(0, pool.Outstanding);
    }

    // ---- helpers ----

    private static async Task<InMemoryChunkedTokenAmount> Write(TokenAmountChunkCodec codec, string text)
    {
        var amount = new InMemoryChunkedTokenAmount();
        await codec.WriteDecimalAsync(text.AsMemory(), amount, Ct);
        return amount;
    }

    private static async Task<InMemoryChunkedTokenAmount> Add(TokenAmountChunkCodec codec, string left, string right)
    {
        var sum = new InMemoryChunkedTokenAmount();
        var digits = await codec.AddAsync(await Write(codec, left), await Write(codec, right), sum, Ct);
        Assert.Equal(digits, sum.Digits);
        return sum;
    }

    private static async Task<InMemoryChunkedTokenAmount> Subtract(TokenAmountChunkCodec codec, string left, string right)
    {
        var difference = new InMemoryChunkedTokenAmount();
        Assert.True(await codec.TrySubtractAsync(await Write(codec, left), await Write(codec, right), difference, Ct));
        return difference;
    }

    private static async Task<byte[]> Chunk(InMemoryChunkedTokenAmount amount, TokenAmountPart part, long seq)
    {
        using var chunk = await amount.ReadChunkAsync(part, seq, Ct);
        return chunk.Memory.ToArray();
    }

    /// <summary>A result is canonical when a full verified read succeeds and rewriting its text gives the same chunks and hash.</summary>
    private static async Task AssertCanonical(TokenAmountChunkCodec codec, InMemoryChunkedTokenAmount amount)
    {
        await codec.VerifyAsync(amount, Ct);
        var text = (await codec.TryReadAsync(amount, long.MaxValue, Ct))!.Value.ToString();
        var rewritten = await Write(codec, text);
        Assert.Equal(rewritten.Digits, amount.Digits);
        Assert.Equal(rewritten.Sha256.ToArray(), amount.Sha256.ToArray());
        Assert.Equal(rewritten.ChunkCount, amount.ChunkCount);
    }

    /// <summary>A random plain decimal, often with leading or trailing zeros, all nines, or a power of ten.</summary>
    private static string RandomDecimal(Random random, int maxDigits)
    {
        var integer = random.Next(6) switch
        {
            0 => "0",
            1 => new string('9', random.Next(1, maxDigits)),
            2 => "1" + new string('0', random.Next(0, maxDigits)),
            3 => new string('0', random.Next(1, 4)) + RandomDigits(random, random.Next(1, maxDigits)),
            _ => RandomDigits(random, random.Next(1, maxDigits)),
        };

        return random.Next(5) switch
        {
            0 => integer,
            1 => integer + "." + new string('9', random.Next(1, maxDigits)),
            2 => integer + "." + RandomDigits(random, random.Next(1, maxDigits)) + new string('0', random.Next(0, 4)),
            3 => integer + "." + new string('0', random.Next(0, maxDigits)) + "1",
            _ => integer + "." + RandomDigits(random, random.Next(1, maxDigits)),
        };
    }

    private static string RandomDigits(Random random, int length)
    {
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            builder.Append((char)('0' + random.Next(10)));
        }

        return builder.ToString();
    }

    /// <summary>The same text with one digit changed, so comparisons often run deep into the chunks.</summary>
    private static string Perturb(Random random, string text)
    {
        var chars = text.ToCharArray();
        var digitPositions = Enumerable.Range(0, chars.Length).Where(i => chars[i] != '.').ToArray();
        var position = digitPositions[random.Next(digitPositions.Length)];
        chars[position] = (char)('0' + random.Next(10));
        return new string(chars);
    }

    /// <summary>The value of a plain decimal as a BigInteger scaled by 10^60 (no test value has more fraction digits).</summary>
    private static BigInteger Reference(string text)
    {
        var point = text.IndexOf('.');
        var integer = point < 0 ? text : text[..point];
        var fraction = point < 0 ? "" : text[(point + 1)..];
        var scaled = integer + fraction.PadRight(_referenceScale, '0');
        return BigInteger.Parse(scaled, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private const int _referenceScale = 60;

    /// <summary>Reads back with the codec that wrote the amount: the chunk size is part of the stored form.</summary>
    private static async Task<BigInteger> Reference(TokenAmountChunkCodec codec, InMemoryChunkedTokenAmount amount)
    {
        var value = await codec.TryReadAsync(amount, long.MaxValue, Ct);
        return Reference(value!.Value.ToString());
    }

    /// <summary>Counts chunk reads.</summary>
    private sealed class CountingReader(ITokenAmountChunkReader inner) : ITokenAmountChunkReader
    {
        public int Reads { get; private set; }

        public TokenAmountDigitCounts Digits => inner.Digits;

        public ReadOnlyMemory<byte> Sha256 => inner.Sha256;

        public ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart part, long seq, CancellationToken ct)
        {
            Reads++;
            return inner.ReadChunkAsync(part, seq, ct);
        }
    }

    /// <summary>Copies each chunk into a buffer from the pool, as a storage reader does, so the pool counts what is held.</summary>
    private sealed class PooledReader(ITokenAmountChunkReader inner, TokenAmountBufferPool pool) : ITokenAmountChunkReader
    {
        public TokenAmountDigitCounts Digits => inner.Digits;

        public ReadOnlyMemory<byte> Sha256 => inner.Sha256;

        public async ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart part, long seq, CancellationToken ct)
        {
            using var source = await inner.ReadChunkAsync(part, seq, ct);
            var copy = pool.Rent(source.Memory.Length);
            source.Memory.CopyTo(copy.Memory);
            return copy;
        }
    }

    /// <summary>Returns a changed copy of one chunk.</summary>
    private sealed class TamperingReader(
        ITokenAmountChunkReader inner, TokenAmountPart part, long seq, Action<byte[]>? change, bool truncate = false) : ITokenAmountChunkReader
    {
        public TokenAmountDigitCounts Digits => inner.Digits;

        public ReadOnlyMemory<byte> Sha256 => inner.Sha256;

        public async ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart readPart, long readSeq, CancellationToken ct)
        {
            using var source = await inner.ReadChunkAsync(readPart, readSeq, ct);
            var bytes = source.Memory.ToArray();
            if (readPart == part && readSeq == seq)
            {
                change?.Invoke(bytes);
                bytes = truncate ? bytes[..^1] : bytes;
            }

            return new Owned(bytes);
        }
    }

    /// <summary>Has no integer chunk 0.</summary>
    private sealed class MissingChunkReader(ITokenAmountChunkReader inner) : ITokenAmountChunkReader
    {
        public TokenAmountDigitCounts Digits => inner.Digits;

        public ReadOnlyMemory<byte> Sha256 => inner.Sha256;

        public ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart part, long seq, CancellationToken ct) =>
            part == TokenAmountPart.IntegerPart && seq == 0
                ? throw new TokenAmountIntegrityException("missing")
                : inner.ReadChunkAsync(part, seq, ct);
    }

    private sealed class Owned(byte[] bytes) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => bytes;

        public void Dispose()
        {
        }
    }
}
