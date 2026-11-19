using System.Buffers;
using System.Security.Cryptography;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// The streamed form of token amounts beyond the PostgreSQL <c>numeric</c> limits (doc 48, DECIDED 11.4, "the disk is the
/// limit"): conversion between <see cref="TokenAmount"/> or decimal text and the chunked layout of
/// <see cref="TokenAmountChunkLayout"/>, comparison, addition and subtraction over chunk readers and writers, and the SHA-256
/// of the packed digits. It has no storage of its own and no database dependency; stores implement
/// <see cref="ITokenAmountChunkReader"/> and <see cref="ITokenAmountChunkWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical form.</b> The header counts are the true digit counts: no leading zero digit in the integer part and no
/// trailing zero digit in the fraction, pads excluded. So the outermost integer chunk starts with a non-zero byte (below 10
/// exactly when the count is odd, because then its high digit is the pad), and the outermost fraction chunk ends with a
/// non-zero byte (a multiple of 10 exactly when the count is odd). Every byte is 0 to 99. A chunk that breaks any of these
/// rules, has the wrong length or is missing is an integrity failure (<see cref="TokenAmountIntegrityException"/>).
/// Results are always written in canonical form, so an amount has exactly one stored representation.
/// </para>
/// <para>
/// <b>Hash order.</b> The SHA-256 covers the packed chunk bytes as stored, chunk after chunk, in the order a walk from the
/// least significant end meets them: fraction chunks from the outermost (highest <c>seq</c>) down to 0, then integer chunks
/// from 0 up to the outermost. This is the order in which addition and subtraction produce their results, so the hash of a
/// new amount is computed while it is written, and every full read (addition, subtraction, materialising, verifying) checks
/// it while reading. Zero has no chunks and the hash of empty input.
/// </para>
/// <para>
/// <b>Memory.</b> Comparison, addition and subtraction hold one chunk per operand plus at most two output chunks, all from
/// <see cref="Pool"/>, whatever the size of the amounts. Long runs of zero chunks in a result are counted, not buffered.
/// </para>
/// <para>Thread-safe: the codec keeps no per-call state.</para>
/// </remarks>
public sealed partial class TokenAmountChunkCodec
{
    /// <summary>A zero chunk to emit for runs of zero bytes, shared and never written to.</summary>
    private static readonly byte[] _zeroChunk = new byte[TokenAmountChunkLayout.StandardChunkBytes];

    /// <summary>The production codec: the 1 MiB layout and the shared buffer pool.</summary>
    public static TokenAmountChunkCodec Standard { get; } = new(TokenAmountChunkLayout.Standard, TokenAmountBufferPool.Shared);

    /// <summary>The SHA-256 of zero, which has no packed digits.</summary>
    public static ReadOnlyMemory<byte> ZeroSha256 { get; } = SHA256.HashData(ReadOnlySpan<byte>.Empty);

    /// <summary>A codec over the production layout with its own buffer pool, so a caller can watch its buffer use.</summary>
    public TokenAmountChunkCodec(TokenAmountBufferPool pool)
        : this(TokenAmountChunkLayout.Standard, pool)
    {
    }

    internal TokenAmountChunkCodec(TokenAmountChunkLayout layout, TokenAmountBufferPool pool)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <summary>The chunk layout this codec reads and writes.</summary>
    public TokenAmountChunkLayout Layout { get; }

    /// <summary>Where every chunk buffer of this codec comes from.</summary>
    public TokenAmountBufferPool Pool { get; }

    private int ChunkBytes => Layout.ChunkBytes;

    /// <summary>
    /// Digit counts no larger than those of the sum of two amounts with these counts: one integer digit more than the
    /// longer integer part (for a carry), and the longer fraction. Known from the headers alone, before any chunk is read.
    /// </summary>
    public static TokenAmountDigitCounts SumBound(TokenAmountDigitCounts left, TokenAmountDigitCounts right) =>
        new(Math.Max(left.IntegerDigits, right.IntegerDigits) + 1, Math.Max(left.FractionDigits, right.FractionDigits));

    /// <summary>
    /// Compares two amounts. The header counts decide first: the amount with more integer digits is larger, and no chunk is
    /// read. Otherwise the chunks are compared from the most significant end, and the walk stops at the first difference.
    /// </summary>
    /// <returns>Negative, zero or positive as <paramref name="left"/> is smaller than, equal to or larger than <paramref name="right"/>.</returns>
    /// <exception cref="TokenAmountIntegrityException">A chunk read on the way is missing or malformed.</exception>
    public async ValueTask<int> CompareAsync(ITokenAmountChunkReader left, ITokenAmountChunkReader right, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var integerDigits = left.Digits.IntegerDigits.CompareTo(right.Digits.IntegerDigits);
        if (integerDigits != 0)
        {
            return integerDigits;
        }

        using var a = new Operand(this, left, hash: false);
        using var b = new Operand(this, right, hash: false);

        // Same integer digit count, so both integer parts have the same chunks with the same lengths and pad.
        for (var seq = a.IntegerChunks - 1; seq >= 0; seq--)
        {
            var aChunk = await a.LoadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false);
            var bChunk = await b.LoadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false);
            var order = aChunk.Span.SequenceCompareTo(bChunk.Span);
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        for (long seq = 0; ; seq++)
        {
            var aHas = seq < a.FractionChunks;
            var bHas = seq < b.FractionChunks;
            if (!aHas || !bHas)
            {
                // Canonical fractions end in a non-zero byte, so the one with digits left is the larger.
                return aHas == bHas ? 0 : aHas ? 1 : -1;
            }

            var aChunk = await a.LoadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
            var bChunk = await b.LoadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
            var common = Math.Min(aChunk.Length, bChunk.Length);
            var order = aChunk.Span[..common].SequenceCompareTo(bChunk.Span[..common]);
            if (order != 0)
            {
                return Math.Sign(order);
            }

            if (aChunk.Length != bChunk.Length)
            {
                // The shorter chunk is its amount's outermost; the other amount still has a non-zero byte to come.
                return aChunk.Length < bChunk.Length ? -1 : 1;
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="left"/> plus <paramref name="right"/> to <paramref name="destination"/>, walking from the least
    /// significant end with a carry. Each operand's hash is checked as it is read; on a mismatch nothing is completed.
    /// </summary>
    /// <returns>The digit counts of the sum.</returns>
    /// <exception cref="TokenAmountIntegrityException">An operand is missing a chunk, is malformed or fails its hash.</exception>
    public ValueTask<TokenAmountDigitCounts> AddAsync(
        ITokenAmountChunkReader left, ITokenAmountChunkReader right, ITokenAmountChunkWriter destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(destination);
        return CombineAsync(left, right, destination, subtract: false, ct);
    }

    /// <summary>
    /// Writes <paramref name="left"/> minus <paramref name="right"/> to <paramref name="destination"/>, walking from the least
    /// significant end with a borrow. A negative amount is never produced: when <paramref name="right"/> is larger, nothing
    /// is written and the result is false. The comparison that decides it reads from the most significant end and stops
    /// at the first difference.
    /// </summary>
    /// <exception cref="TokenAmountIntegrityException">An operand is missing a chunk, is malformed or fails its hash.</exception>
    public async ValueTask<bool> TrySubtractAsync(
        ITokenAmountChunkReader left, ITokenAmountChunkReader right, ITokenAmountChunkWriter destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(destination);

        if (await CompareAsync(left, right, ct).ConfigureAwait(false) < 0)
        {
            return false;
        }

        await CombineAsync(left, right, destination, subtract: true, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Writes an in-memory amount in the chunked form. Any size <see cref="TokenAmount"/> can hold.</summary>
    /// <returns>The digit counts written.</returns>
    public ValueTask<TokenAmountDigitCounts> WriteAsync(TokenAmount amount, ITokenAmountChunkWriter destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return WriteDecimalAsync(amount.ToString().AsMemory(), destination, ct);
    }

    /// <summary>
    /// Writes a plain decimal (ASCII digits, optionally <c>.</c> and more digits; no sign, exponent or spaces) of any length in
    /// canonical chunked form: leading integer zeros and trailing fraction zeros are dropped. No big-number arithmetic is done,
    /// so the text length is the only cost.
    /// </summary>
    /// <returns>The digit counts written.</returns>
    /// <exception cref="FormatException">The text is not a plain decimal.</exception>
    public async ValueTask<TokenAmountDigitCounts> WriteDecimalAsync(
        ReadOnlyMemory<char> text, ITokenAmountChunkWriter destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var (integer, fraction) = SplitPlainDecimal(text);
        var digits = new TokenAmountDigitCounts(integer.Length, fraction.Length);

        using var emitter = new Emitter(destination);
        using var buffer = Pool.Rent(ChunkBytes);

        for (var seq = Layout.ChunkCount(fraction.Length) - 1; seq >= 0; seq--)
        {
            ct.ThrowIfCancellationRequested();
            var length = Layout.ChunkLength(fraction.Length, seq);
            PackFraction(fraction.Span, seq * ChunkBytes, buffer.Memory.Span[..length]);
            await emitter.EmitAsync(TokenAmountPart.FractionPart, seq, buffer.Memory[..length], ct).ConfigureAwait(false);
        }

        for (long seq = 0; seq < Layout.ChunkCount(integer.Length); seq++)
        {
            ct.ThrowIfCancellationRequested();
            var length = Layout.ChunkLength(integer.Length, seq);
            PackInteger(integer.Span, seq * ChunkBytes, buffer.Memory.Span[..length]);
            await emitter.EmitAsync(TokenAmountPart.IntegerPart, seq, buffer.Memory[..length], ct).ConfigureAwait(false);
        }

        await emitter.CompleteAsync(digits, ct).ConfigureAwait(false);
        return digits;
    }

    /// <summary>
    /// Reads a stored amount in full into a <see cref="TokenAmount"/>, checking its structure and hash, but only when its
    /// digit counts together are at most <paramref name="maxTotalDigits"/> (the operator's in-memory maximum). Above it the
    /// amount is never materialised and the result is null, with no chunk read.
    /// </summary>
    /// <exception cref="TokenAmountIntegrityException">A chunk is missing or malformed, or the hash does not match.</exception>
    public async ValueTask<TokenAmount?> TryReadAsync(ITokenAmountChunkReader source, long maxTotalDigits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var digits = source.Digits;
        if (!digits.TotalAtMost(Math.Min(maxTotalDigits, int.MaxValue - 2)))
        {
            return null;
        }

        var integerDigits = (int)digits.IntegerDigits;
        var fractionDigits = (int)digits.FractionDigits;
        if (digits.IsZero)
        {
            using var none = new Operand(this, source, hash: true);
            none.VerifyHash();
            return TokenAmount.Zero;
        }

        // Text form: the integer digits (or "0"), then "." and the fraction digits when there are any.
        var integerLength = Math.Max(integerDigits, 1);
        var text = new char[integerLength + (fractionDigits > 0 ? 1 + fractionDigits : 0)];
        text[0] = '0';
        if (fractionDigits > 0)
        {
            text[integerLength] = '.';
        }

        using (var operand = new Operand(this, source, hash: true))
        {
            for (var seq = operand.FractionChunks - 1; seq >= 0; seq--)
            {
                var chunk = await operand.LoadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
                UnpackFraction(chunk.Span, seq * ChunkBytes, text.AsSpan(integerLength + 1, fractionDigits));
            }

            for (long seq = 0; seq < operand.IntegerChunks; seq++)
            {
                var chunk = await operand.LoadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false);
                UnpackInteger(chunk.Span, seq * ChunkBytes, text.AsSpan(0, integerDigits));
            }

            operand.Release();
            operand.VerifyHash();
        }

        return TokenAmount.Parse(text, new TokenAmountBounds(integerLength, fractionDigits));
    }

    /// <summary>
    /// Reads a stored amount in full, one chunk at a time, and checks every chunk's length and canonical form and the hash.
    /// </summary>
    /// <exception cref="TokenAmountIntegrityException">The amount fails a check.</exception>
    public async ValueTask VerifyAsync(ITokenAmountChunkReader source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var operand = new Operand(this, source, hash: true);
        for (var seq = operand.FractionChunks - 1; seq >= 0; seq--)
        {
            await operand.LoadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
        }

        for (long seq = 0; seq < operand.IntegerChunks; seq++)
        {
            await operand.LoadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false);
        }

        operand.Release();
        operand.VerifyHash();
    }

    private async ValueTask<TokenAmountDigitCounts> CombineAsync(
        ITokenAmountChunkReader left, ITokenAmountChunkReader right, ITokenAmountChunkWriter destination, bool subtract, CancellationToken ct)
    {
        using var a = new Operand(this, left, hash: true);
        using var b = new Operand(this, right, hash: true);
        using var emitter = new Emitter(destination);
        using var sink = new IntegerSink(this, emitter);
        var chunkBytes = ChunkBytes;
        var carry = 0;

        // Fraction: from the least significant byte towards the point. Trailing zero bytes of the result are not written;
        // the first non-zero byte met fixes the result's fraction length.
        var fractionBytes = Math.Max(
            TokenAmountChunkLayout.PackedBytes(a.Digits.FractionDigits), TokenAmountChunkLayout.PackedBytes(b.Digits.FractionDigits));
        long resultFractionBytes = 0;
        byte lastFractionByte = 0;
        for (var seq = Layout.ChunkCount(fractionBytes * 2) - 1; seq >= 0; seq--)
        {
            var length = (int)Math.Min(chunkBytes, fractionBytes - (seq * chunkBytes));
            var aChunk = await a.LoadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
            var bChunk = await b.LoadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
            var output = sink.Scratch[..length];
            var aSpan = aChunk.Span;
            var bSpan = bChunk.Span;
            for (var index = length - 1; index >= 0; index--)
            {
                int av = index < aSpan.Length ? aSpan[index] : 0;
                int bv = index < bSpan.Length ? bSpan[index] : 0;
                var digit = Step(av, bv, ref carry, subtract);
                output[index] = digit;
                if (resultFractionBytes == 0 && digit != 0)
                {
                    resultFractionBytes = (seq * chunkBytes) + index + 1;
                    lastFractionByte = digit;
                }
            }

            if (resultFractionBytes != 0)
            {
                var emitLength = (int)Math.Min(length, resultFractionBytes - (seq * chunkBytes));
                await emitter.EmitAsync(TokenAmountPart.FractionPart, seq, sink.ScratchMemory[..emitLength], ct).ConfigureAwait(false);
            }
        }

        // Integer: from the point outward; the sink drops leading zero bytes, however many chunks they span.
        var integerBytes = Math.Max(
            TokenAmountChunkLayout.PackedBytes(a.Digits.IntegerDigits), TokenAmountChunkLayout.PackedBytes(b.Digits.IntegerDigits));
        var integerChunks = Layout.ChunkCount(integerBytes * 2);
        for (long seq = 0; seq < integerChunks; seq++)
        {
            var length = (int)Math.Min(chunkBytes, integerBytes - (seq * chunkBytes));
            var aChunk = await a.LoadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false);
            var bChunk = await b.LoadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false);
            var output = sink.Scratch;
            var aSpan = aChunk.Span;
            var bSpan = bChunk.Span;
            for (var k = 0; k < length; k++)
            {
                int av = k < aSpan.Length ? aSpan[aSpan.Length - 1 - k] : 0;
                int bv = k < bSpan.Length ? bSpan[bSpan.Length - 1 - k] : 0;
                output[k] = Step(av, bv, ref carry, subtract);
            }

            if (carry != 0 && !subtract && seq == integerChunks - 1 && length < chunkBytes)
            {
                // The carry out of the top byte fits the outermost chunk.
                output[length++] = (byte)carry;
                carry = 0;
            }

            await sink.CompleteChunkAsync(length, ct).ConfigureAwait(false);
        }

        if (carry != 0)
        {
            if (subtract)
            {
                // Left was compared as at least right, so a borrow out of the top means the data changed under the walk.
                throw new TokenAmountIntegrityException("A stored token amount changed while it was being subtracted.");
            }

            // The carry starts a new outermost chunk (the top chunk was full, or the operands had no integer part).
            sink.Scratch[0] = (byte)carry;
            await sink.CompleteChunkAsync(1, ct).ConfigureAwait(false);
        }

        a.Release();
        b.Release();
        a.VerifyHash();
        b.VerifyHash();

        var (integerResultBytes, topByte) = await sink.FinishAsync(ct).ConfigureAwait(false);
        var digits = new TokenAmountDigitCounts(
            integerResultBytes == 0 ? 0 : (integerResultBytes * 2) - (topByte < 10 ? 1 : 0),
            resultFractionBytes == 0 ? 0 : (resultFractionBytes * 2) - (lastFractionByte % 10 == 0 ? 1 : 0));
        await emitter.CompleteAsync(digits, ct).ConfigureAwait(false);
        return digits;
    }

    /// <summary>One base-100 digit pair of a sum or difference, updating the carry or borrow.</summary>
    private static byte Step(int a, int b, ref int carry, bool subtract)
    {
        if (subtract)
        {
            var difference = a - b - carry;
            carry = difference < 0 ? 1 : 0;
            return (byte)(difference + (carry * 100));
        }

        var sum = a + b + carry;
        carry = sum >= 100 ? 1 : 0;
        return (byte)(sum - (carry * 100));
    }

    /// <summary>Checks one chunk against the layout and the canonical form for its part.</summary>
    private void Validate(TokenAmountPart part, long digits, long seq, ReadOnlySpan<byte> chunk)
    {
        var expected = Layout.ChunkLength(digits, seq);
        if (chunk.Length != expected)
        {
            throw new TokenAmountIntegrityException(
                $"Chunk {seq} of the {part} part has {chunk.Length} bytes; the header says {expected}.");
        }

        if (chunk.IndexOfAnyInRange((byte)100, byte.MaxValue) >= 0)
        {
            throw new TokenAmountIntegrityException($"Chunk {seq} of the {part} part holds a byte that is not a base-100 digit pair.");
        }

        if (seq != Layout.ChunkCount(digits) - 1)
        {
            return;
        }

        var odd = (digits & 1) == 1;
        if (part == TokenAmountPart.IntegerPart)
        {
            var top = chunk[0];
            if (top == 0 || (odd ? top >= 10 : top < 10))
            {
                throw new TokenAmountIntegrityException("The integer part does not start with the digit the header count implies.");
            }
        }
        else
        {
            var last = chunk[^1];
            if (last == 0 || (odd ? last % 10 != 0 : last % 10 == 0))
            {
                throw new TokenAmountIntegrityException("The fraction does not end with the digit the header count implies.");
            }
        }
    }

    /// <summary>Validates a plain decimal and returns its digits without leading integer zeros or trailing fraction zeros.</summary>
    private static (ReadOnlyMemory<char> Integer, ReadOnlyMemory<char> Fraction) SplitPlainDecimal(ReadOnlyMemory<char> text)
    {
        var span = text.Span;
        var point = span.IndexOf('.');
        var integerLength = point < 0 ? span.Length : point;
        if (integerLength == 0 || span[..integerLength].ContainsAnyExceptInRange('0', '9'))
        {
            throw new FormatException("Not a plain decimal: expected one or more ASCII digits before an optional '.'.");
        }

        var fraction = ReadOnlyMemory<char>.Empty;
        if (point >= 0)
        {
            var after = span[(point + 1)..];
            if (after.IsEmpty || after.ContainsAnyExceptInRange('0', '9'))
            {
                throw new FormatException("Not a plain decimal: expected one or more ASCII digits after the '.'.");
            }

            fraction = text.Slice(point + 1, after.TrimEnd('0').Length);
        }

        var integer = text[..integerLength];
        var leadingZeros = integerLength - span[..integerLength].TrimStart('0').Length;
        return (integer[leadingZeros..], fraction);
    }

    /// <summary>Packs fraction bytes starting at byte offset <paramref name="first"/> from the point.</summary>
    private static void PackFraction(ReadOnlySpan<char> fraction, long first, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var position = (first + index) * 2;
            var high = fraction[(int)position] - '0';
            var low = position + 1 < fraction.Length ? fraction[(int)position + 1] - '0' : 0;
            destination[index] = (byte)((high * 10) + low);
        }
    }

    /// <summary>Packs integer bytes for offsets <paramref name="first"/> onward (from the point), most significant first.</summary>
    private static void PackInteger(ReadOnlySpan<char> integer, long first, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var offset = first + (destination.Length - 1 - index);
            var lowPosition = integer.Length - 1 - (offset * 2);
            var high = lowPosition - 1 >= 0 ? integer[(int)lowPosition - 1] - '0' : 0;
            var low = integer[(int)lowPosition] - '0';
            destination[index] = (byte)((high * 10) + low);
        }
    }

    private static void UnpackFraction(ReadOnlySpan<byte> chunk, long first, Span<char> fraction)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            var position = (int)((first + index) * 2);
            fraction[position] = (char)('0' + (chunk[index] / 10));
            if (position + 1 < fraction.Length)
            {
                fraction[position + 1] = (char)('0' + (chunk[index] % 10));
            }
        }
    }

    private static void UnpackInteger(ReadOnlySpan<byte> chunk, long first, Span<char> integer)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            var offset = first + (chunk.Length - 1 - index);
            var lowPosition = (int)(integer.Length - 1 - (offset * 2));
            integer[lowPosition] = (char)('0' + (chunk[index] % 10));
            if (lowPosition - 1 >= 0)
            {
                integer[lowPosition - 1] = (char)('0' + (chunk[index] / 10));
            }
        }
    }

    /// <summary>One operand of a walk: holds at most one chunk, validates each, and hashes a full walk in hash order.</summary>
    private sealed class Operand : IDisposable
    {
        private readonly TokenAmountChunkCodec _codec;
        private readonly ITokenAmountChunkReader _reader;
        private readonly IncrementalHash? _hash;
        private IMemoryOwner<byte>? _current;

        public Operand(TokenAmountChunkCodec codec, ITokenAmountChunkReader reader, bool hash)
        {
            _codec = codec;
            _reader = reader;
            Digits = reader.Digits;
            IntegerChunks = codec.Layout.ChunkCount(Digits.IntegerDigits);
            FractionChunks = codec.Layout.ChunkCount(Digits.FractionDigits);
            _hash = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        }

        public TokenAmountDigitCounts Digits { get; }

        public long IntegerChunks { get; }

        public long FractionChunks { get; }

        /// <summary>Releases the held chunk and loads the next; empty when the operand has no such chunk.</summary>
        public async ValueTask<ReadOnlyMemory<byte>> LoadAsync(TokenAmountPart part, long seq, CancellationToken ct)
        {
            Release();
            ct.ThrowIfCancellationRequested();
            if (seq >= (part == TokenAmountPart.IntegerPart ? IntegerChunks : FractionChunks))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            var owner = await _reader.ReadChunkAsync(part, seq, ct).ConfigureAwait(false);
            _current = owner;
            ReadOnlyMemory<byte> chunk = owner.Memory;
            _codec.Validate(part, Digits.Of(part), seq, chunk.Span);
            _hash?.AppendData(chunk.Span);
            return chunk;
        }

        public void Release()
        {
            _current?.Dispose();
            _current = null;
        }

        /// <summary>After a full walk in hash order: the computed hash must equal the recorded one, when one is recorded.</summary>
        public void VerifyHash()
        {
            if (_hash is null)
            {
                throw new InvalidOperationException("This walk does not hash.");
            }

            var computed = _hash.GetHashAndReset();
            var recorded = _reader.Sha256;
            if (!recorded.IsEmpty && !computed.AsSpan().SequenceEqual(recorded.Span))
            {
                throw new TokenAmountIntegrityException("A stored token amount does not match its SHA-256.");
            }
        }

        public void Dispose()
        {
            Release();
            _hash?.Dispose();
        }
    }

    /// <summary>Passes chunks to the destination and hashes them in the order written.</summary>
    private sealed class Emitter(ITokenAmountChunkWriter destination) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public ValueTask EmitAsync(TokenAmountPart part, long seq, ReadOnlyMemory<byte> chunk, CancellationToken ct)
        {
            _hash.AppendData(chunk.Span);
            return destination.WriteChunkAsync(part, seq, chunk, ct);
        }

        public ValueTask CompleteAsync(TokenAmountDigitCounts digits, CancellationToken ct) =>
            destination.CompleteAsync(digits, _hash.GetHashAndReset(), ct);

        public void Dispose() => _hash.Dispose();
    }

    /// <summary>
    /// Collects integer result bytes, least significant first, one chunk at a time, and writes them in canonical form. A
    /// chunk of zeros is only counted, since it may turn out to be leading zeros; the last non-zero chunk is held until it is
    /// known whether it is the outermost, which is trimmed of its leading zero bytes.
    /// </summary>
    private sealed class IntegerSink : IDisposable
    {
        private readonly TokenAmountChunkCodec _codec;
        private readonly Emitter _emitter;
        private IMemoryOwner<byte> _fill;
        private IMemoryOwner<byte>? _held;
        private long _nextSeq;
        private long _heldSeq;
        private int _heldLength;
        private long _pendingZeroChunks;

        public IntegerSink(TokenAmountChunkCodec codec, Emitter emitter)
        {
            _codec = codec;
            _emitter = emitter;
            _fill = codec.Pool.Rent(codec.ChunkBytes);
        }

        /// <summary>The buffer being filled, least significant byte first. The fraction walk borrows it before the integer walk.</summary>
        public Span<byte> Scratch => _fill.Memory.Span;

        public Memory<byte> ScratchMemory => _fill.Memory;

        /// <summary>The scratch holds <paramref name="length"/> bytes of the next integer chunk.</summary>
        public async ValueTask CompleteChunkAsync(int length, CancellationToken ct)
        {
            var seq = _nextSeq++;
            if (!_fill.Memory.Span[..length].ContainsAnyExcept((byte)0))
            {
                _pendingZeroChunks++;
                return;
            }

            // Something non-zero follows the held chunk, so the held chunk and the zero chunks after it are inner chunks.
            await FlushHeldAsync(_codec.ChunkBytes, ct).ConfigureAwait(false);
            await FlushZerosAsync(ct).ConfigureAwait(false);
            _held ??= _codec.Pool.Rent(_codec.ChunkBytes);
            (_held, _fill) = (_fill, _held);
            _heldSeq = seq;
            _heldLength = length;
        }

        /// <summary>Writes the outermost chunk, trimmed; trailing zero chunks are leading zeros and are dropped.</summary>
        /// <returns>The integer byte count and the most significant byte (zero when the integer part is zero).</returns>
        public async ValueTask<(long Bytes, byte Top)> FinishAsync(CancellationToken ct)
        {
            if (_held is null || _heldLength == 0)
            {
                return (0, 0);
            }

            var span = _held.Memory.Span[.._heldLength];
            var top = span.LastIndexOfAnyExcept((byte)0);
            var length = top + 1;
            var topByte = span[top];
            await FlushHeldAsync(length, ct).ConfigureAwait(false);
            return ((_heldSeq * _codec.ChunkBytes) + length, topByte);
        }

        private async ValueTask FlushHeldAsync(int length, CancellationToken ct)
        {
            if (_held is null || _heldLength == 0)
            {
                return;
            }

            if (length != _heldLength && length > _heldLength)
            {
                throw new InvalidOperationException("Only the outermost integer chunk may be shorter than a full chunk.");
            }

            var memory = _held.Memory[..length];
            memory.Span.Reverse();
            await _emitter.EmitAsync(TokenAmountPart.IntegerPart, _heldSeq, memory, ct).ConfigureAwait(false);
            _heldLength = 0;
        }

        private async ValueTask FlushZerosAsync(CancellationToken ct)
        {
            var zero = _zeroChunk.AsMemory(0, _codec.ChunkBytes);
            for (var seq = _nextSeq - 1 - _pendingZeroChunks; _pendingZeroChunks > 0; _pendingZeroChunks--, seq++)
            {
                await _emitter.EmitAsync(TokenAmountPart.IntegerPart, seq, zero, ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _fill.Dispose();
            _held?.Dispose();
        }
    }
}
