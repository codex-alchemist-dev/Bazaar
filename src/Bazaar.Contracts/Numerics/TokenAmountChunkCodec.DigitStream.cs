using System.Buffers;

namespace Bazaar.Contracts.Numerics;

/// <summary>The digit-stream intake of the chunk codec (doc 48, DECIDED 11.4, "Transport above the frame size").</summary>
public sealed partial class TokenAmountChunkCodec
{
    /// <summary>Bytes read from the source per read: a fixed buffer, whatever the length of the amount.</summary>
    private const int _streamReadBytes = 64 * 1024;

    /// <summary>
    /// Writes a plain decimal read from <paramref name="source"/> (ASCII digits, optionally one <c>.</c> and more digits; no
    /// sign, exponent, space or line ending) of any length in canonical chunked form, never holding more than a few chunks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two passes. The digits are first written to <paramref name="scratch"/> as they arrive, packed two per byte in reading
    /// order, because the integer part cannot be aligned at the point before its length is known. Leading integer zeros are
    /// dropped as they arrive. Then the scratch is read back one chunk at a time: the fraction chunks are already aligned at
    /// the point and are written outermost first, trimmed of trailing zeros, and the integer chunks are aligned and written
    /// from the point outward. So <paramref name="destination"/> receives every chunk once, in hash order, and is completed
    /// with the true counts and the SHA-256, like every other result of this codec.
    /// </para>
    /// <para>
    /// <paramref name="guard"/> counts the digits as sent, zeros included, on each side of the point; the read stops with
    /// <see cref="TokenAmountIntakeFailure.OverGuard"/> at the first digit beyond it. <see cref="TokenAmountBounds.Unlimited"/>
    /// sets no count limit. Any byte other than a digit or one <c>.</c>, or no digit on a side of the point, stops it with
    /// <see cref="TokenAmountIntakeFailure.Malformed"/>. Either way the destination is not completed.
    /// </para>
    /// </remarks>
    /// <returns>The digit counts written.</returns>
    /// <exception cref="TokenAmountIntakeException">The text is malformed or over the guard.</exception>
    /// <exception cref="TokenAmountIntegrityException">The scratch did not return what was written to it.</exception>
    public async ValueTask<TokenAmountDigitCounts> WriteDecimalStreamAsync(
        Stream source, TokenAmountBounds guard, ITokenAmountDigitScratch scratch, ITokenAmountChunkWriter destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(destination);

        var digits = await StageDigitStreamAsync(source, guard, scratch, ct).ConfigureAwait(false);
        await scratch.FlushAsync(ct).ConfigureAwait(false);

        using var emitter = new Emitter(destination);
        await EmitStagedFractionAsync(scratch, digits.FractionDigits, emitter, ct).ConfigureAwait(false);
        await EmitStagedIntegerAsync(scratch, digits.IntegerDigits, emitter, ct).ConfigureAwait(false);
        await emitter.CompleteAsync(digits, ct).ConfigureAwait(false);
        return digits;
    }

    /// <summary>Pass one: reads the source into scratch chunks. Returns the canonical counts.</summary>
    private async ValueTask<TokenAmountDigitCounts> StageDigitStreamAsync(
        Stream source, TokenAmountBounds guard, ITokenAmountDigitScratch scratch, CancellationToken ct)
    {
        var state = new DigitStreamState(
            guard.IsUnlimited ? long.MaxValue : guard.MaxIntegerDigits,
            guard.IsUnlimited ? long.MaxValue : guard.MaxFractionDigits,
            ChunkBytes);
        using var read = Pool.Rent(_streamReadBytes);
        using var fill = Pool.Rent(ChunkBytes);

        while (true)
        {
            var got = await source.ReadAsync(read.Memory, ct).ConfigureAwait(false);
            if (got == 0)
            {
                break;
            }

            var offset = 0;
            while (offset < got)
            {
                offset += state.Consume(read.Memory.Span[offset..got], fill.Memory.Span);
                if (state.FillFull || state.PointReached)
                {
                    // A full chunk, or the end of the integer part: the fill goes to scratch before more digits arrive.
                    await FlushFillAsync(state, fill, scratch, ct).ConfigureAwait(false);
                    state.PointReached = false;
                }
            }
        }

        if (state.IntegerChars == 0)
        {
            throw new TokenAmountIntakeException(TokenAmountIntakeFailure.Malformed, "Not a plain decimal: expected one or more ASCII digits before an optional '.'.");
        }

        if (state.InFraction && state.FractionChars == 0)
        {
            throw new TokenAmountIntakeException(TokenAmountIntakeFailure.Malformed, "Not a plain decimal: expected one or more ASCII digits after the '.'.");
        }

        await FlushFillAsync(state, fill, scratch, ct).ConfigureAwait(false);
        return new TokenAmountDigitCounts(state.IntegerDigits, state.LastNonZeroFraction);
    }

    private static async ValueTask FlushFillAsync(DigitStreamState state, IMemoryOwner<byte> fill, ITokenAmountDigitScratch scratch, CancellationToken ct)
    {
        if (state.FillDigits == 0)
        {
            return;
        }

        var part = state.FillPart;
        var bytes = (state.FillDigits + 1) / 2;
        await scratch.WriteAsync(part, state.NextSeq(part), fill.Memory[..bytes], ct).ConfigureAwait(false);
        state.FillDigits = 0;
    }

    /// <summary>Pass two, fraction: the scratch chunks are aligned already; written outermost first, trimmed of trailing zeros.</summary>
    private async ValueTask EmitStagedFractionAsync(ITokenAmountDigitScratch scratch, long fractionDigits, Emitter emitter, CancellationToken ct)
    {
        for (var seq = Layout.ChunkCount(fractionDigits) - 1; seq >= 0; seq--)
        {
            var length = Layout.ChunkLength(fractionDigits, seq);
            using var chunk = await scratch.ReadAsync(TokenAmountPart.FractionPart, seq, ct).ConfigureAwait(false);
            if (chunk.Memory.Length < length)
            {
                throw new TokenAmountIntegrityException($"Scratch chunk {seq} of the fraction is shorter than was written.");
            }

            await emitter.EmitAsync(TokenAmountPart.FractionPart, seq, chunk.Memory[..length], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pass two, integer: each stored chunk, from the point outward, is assembled from at most two scratch chunks (read in
    /// reading order from the top), which are the only scratch chunks held.
    /// </summary>
    private async ValueTask EmitStagedIntegerAsync(ITokenAmountDigitScratch scratch, long integerDigits, Emitter emitter, CancellationToken ct)
    {
        var chunkBytes = ChunkBytes;
        using var output = Pool.Rent(chunkBytes);
        var held = new HeldScratch[2];
        try
        {
            for (long seq = 0; seq < Layout.ChunkCount(integerDigits); seq++)
            {
                var length = Layout.ChunkLength(integerDigits, seq);

                // The scratch chunks this stored chunk draws on, found from the top indices of its least and most
                // significant digits (the scratch is in reading order, from the top).
                var firstByteOffset = seq * chunkBytes;
                var lowSeq = (integerDigits - 1 - (2 * firstByteOffset)) / 2 / chunkBytes;
                var highSeq = Math.Max(0, integerDigits - (2 * (firstByteOffset + length))) / 2 / chunkBytes;

                // Release what this chunk does not need (walking outward is walking towards the top, so it is never needed
                // again), then read what is missing into the freed slots.
                for (var slot = 0; slot < held.Length; slot++)
                {
                    if (held[slot].Owner is { } owner && held[slot].Seq != lowSeq && held[slot].Seq != highSeq)
                    {
                        owner.Dispose();
                        held[slot] = default;
                    }
                }

                await HoldAsync(scratch, held, lowSeq, ct).ConfigureAwait(false);
                await HoldAsync(scratch, held, highSeq, ct).ConfigureAwait(false);

                var low = held[0].Owner is not null && held[0].Seq == lowSeq ? held[0] : held[1];
                var high = held[0].Owner is not null && held[0].Seq == highSeq ? held[0] : held[1];
                Align(
                    output.Memory.Span[..length], firstByteOffset, integerDigits, chunkBytes,
                    low.Seq, low.Owner!.Memory.Span, high.Seq, high.Owner!.Memory.Span);
                await emitter.EmitAsync(TokenAmountPart.IntegerPart, seq, output.Memory[..length], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var slot in held)
            {
                slot.Owner?.Dispose();
            }
        }
    }

    /// <summary>Reads scratch chunk <paramref name="seq"/> into a free slot unless a slot holds it already.</summary>
    private static async ValueTask HoldAsync(ITokenAmountDigitScratch scratch, HeldScratch[] held, long seq, CancellationToken ct)
    {
        if (held.Any(slot => slot.Owner is not null && slot.Seq == seq))
        {
            return;
        }

        var free = held[0].Owner is null ? 0 : 1;
        held[free] = new HeldScratch(seq, await scratch.ReadAsync(TokenAmountPart.IntegerPart, seq, ct).ConfigureAwait(false));
    }

    /// <summary>One scratch chunk held during pass two.</summary>
    private readonly record struct HeldScratch(long Seq, IMemoryOwner<byte>? Owner);

    /// <summary>
    /// Fills one stored integer chunk (most significant byte first) whose least significant byte is <paramref name="firstByteOffset"/>
    /// bytes from the point, from the scratch chunks <paramref name="lowSeq"/> and <paramref name="highSeq"/>.
    /// </summary>
    private static void Align(
        Span<byte> output, long firstByteOffset, long integerDigits, int chunkBytes,
        long lowSeq, ReadOnlySpan<byte> low, long highSeq, ReadOnlySpan<byte> high)
    {
        for (var index = 0; index < output.Length; index++)
        {
            var byteOffset = firstByteOffset + (output.Length - 1 - index);
            var lowDigit = DigitFromTop(integerDigits - 1 - (2 * byteOffset), chunkBytes, lowSeq, low, highSeq, high);
            var highDigit = DigitFromTop(integerDigits - 2 - (2 * byteOffset), chunkBytes, lowSeq, low, highSeq, high);
            output[index] = (byte)((highDigit * 10) + lowDigit);
        }
    }

    /// <summary>The digit at <paramref name="topIndex"/> from the top of the scratch integer; zero above the top (the pad).</summary>
    private static int DigitFromTop(long topIndex, int chunkBytes, long lowSeq, ReadOnlySpan<byte> low, long highSeq, ReadOnlySpan<byte> high)
    {
        if (topIndex < 0)
        {
            return 0;
        }

        var packed = topIndex / 2;
        var seq = packed / chunkBytes;
        var within = (int)(packed % chunkBytes);
        var chunk = seq == lowSeq ? low : seq == highSeq ? high : throw new InvalidOperationException("A scratch chunk that is not held was needed.");
        if (within >= chunk.Length)
        {
            throw new TokenAmountIntegrityException($"Scratch chunk {seq} of the integer part is shorter than was written.");
        }

        var value = chunk[within];
        return (topIndex & 1) == 0 ? value / 10 : value % 10;
    }

    /// <summary>The state of pass one: where the stream is and what the fill buffer holds.</summary>
    private sealed class DigitStreamState(long maxIntegerChars, long maxFractionChars, int chunkBytes)
    {
        private readonly int _fillCapacityDigits = chunkBytes * 2;
        private long _integerSeq;
        private long _fractionSeq;
        private long _position;

        public bool InFraction { get; private set; }

        /// <summary>Set when the point was consumed; the integer digits in the fill are flushed before any fraction digit.</summary>
        public bool PointReached { get; set; }

        public long IntegerChars { get; private set; }

        public long FractionChars { get; private set; }

        /// <summary>Integer digits kept: those from the first non-zero digit on.</summary>
        public long IntegerDigits { get; private set; }

        /// <summary>The fraction digit count up to and including its last non-zero digit.</summary>
        public long LastNonZeroFraction { get; private set; }

        /// <summary>Digits in the fill buffer, packed two per byte.</summary>
        public int FillDigits { get; set; }

        public bool FillFull => FillDigits == _fillCapacityDigits;

        /// <summary>The part the fill buffer holds: the integer part until the point has been flushed.</summary>
        public TokenAmountPart FillPart => InFraction && !PointReached ? TokenAmountPart.FractionPart : TokenAmountPart.IntegerPart;

        public long NextSeq(TokenAmountPart part) => part == TokenAmountPart.IntegerPart ? _integerSeq++ : _fractionSeq++;

        /// <summary>
        /// Consumes bytes until the input ends, the fill is full or the point is reached. Returns the bytes consumed.
        /// </summary>
        /// <exception cref="TokenAmountIntakeException">A byte is not allowed, or a count is over the guard.</exception>
        public int Consume(ReadOnlySpan<byte> input, Span<byte> fill)
        {
            for (var index = 0; index < input.Length; index++)
            {
                var value = input[index];
                _position++;
                if (value == (byte)'.')
                {
                    if (InFraction || IntegerChars == 0)
                    {
                        throw new TokenAmountIntakeException(
                            TokenAmountIntakeFailure.Malformed,
                            $"Not a plain decimal: '.' at byte {_position} needs one or more digits before it and may appear once.");
                    }

                    InFraction = true;
                    PointReached = true;
                    return index + 1;
                }

                var digit = value - '0';
                if ((uint)digit > 9)
                {
                    throw new TokenAmountIntakeException(
                        TokenAmountIntakeFailure.Malformed, $"Not a plain decimal: byte {_position} is not an ASCII digit or '.'.");
                }

                if (InFraction)
                {
                    if (++FractionChars > maxFractionChars)
                    {
                        throw OverGuard("after", maxFractionChars);
                    }

                    if (digit != 0)
                    {
                        LastNonZeroFraction = FractionChars;
                    }
                }
                else
                {
                    if (++IntegerChars > maxIntegerChars)
                    {
                        throw OverGuard("before", maxIntegerChars);
                    }

                    if (IntegerDigits == 0 && digit == 0)
                    {
                        // A leading zero: counted against the guard, never stored.
                        continue;
                    }

                    IntegerDigits++;
                }

                var at = FillDigits >> 1;
                fill[at] = (FillDigits & 1) == 0 ? (byte)(digit * 10) : (byte)(fill[at] + digit);
                if (++FillDigits == _fillCapacityDigits)
                {
                    return index + 1;
                }
            }

            return input.Length;
        }

        private static TokenAmountIntakeException OverGuard(string side, long max) => new(
            TokenAmountIntakeFailure.OverGuard,
            $"The amount has more than {max} digits {side} the point, more than agent.budget.amount_intake_max_digits allows; it was not read further.");
    }
}
