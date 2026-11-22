namespace Bazaar.Contracts.Numerics;

/// <summary>
/// Which side of the decimal point a chunk of a large token amount holds (doc 48, DECIDED 11.4). The numeric values are the
/// stored <c>part</c> column of <c>token_amount_large_chunk</c>, so they never change.
/// </summary>
public enum TokenAmountPart : short
{
    /// <summary>Digits before the decimal point. Chunks are numbered from the point outward.</summary>
    IntegerPart = 0,

    /// <summary>Digits after the decimal point. Chunks are numbered from the point onward.</summary>
    FractionPart = 1,
}

/// <summary>
/// The true digit counts of a canonical amount in the chunked form (doc 48, DECIDED 11.4): digits before the point without
/// leading zeros, and digits after it without trailing zeros. Pad digits are never counted. Zero is (0, 0). The counts are
/// 64-bit, so they never bound an amount that a disk can hold.
/// </summary>
public readonly record struct TokenAmountDigitCounts
{
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public TokenAmountDigitCounts(long integerDigits, long fractionDigits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(integerDigits);
        ArgumentOutOfRangeException.ThrowIfNegative(fractionDigits);
        IntegerDigits = integerDigits;
        FractionDigits = fractionDigits;
    }

    /// <summary>Digits before the decimal point, without leading zeros.</summary>
    public long IntegerDigits { get; }

    /// <summary>Digits after the decimal point, without trailing zeros.</summary>
    public long FractionDigits { get; }

    /// <summary>True for the amount zero.</summary>
    public bool IsZero => IntegerDigits == 0 && FractionDigits == 0;

    /// <summary>The digit count of one part.</summary>
    public long Of(TokenAmountPart part) => part == TokenAmountPart.IntegerPart ? IntegerDigits : FractionDigits;

    /// <summary>
    /// True when both counts together are at most <paramref name="maxTotalDigits"/>. Computed without overflow, so it is
    /// exact for every pair of counts.
    /// </summary>
    public bool TotalAtMost(long maxTotalDigits) =>
        maxTotalDigits >= 0 && IntegerDigits <= maxTotalDigits && FractionDigits <= maxTotalDigits - IntegerDigits;

    /// <summary>The counts of an in-memory amount.</summary>
    public static TokenAmountDigitCounts Of(TokenAmount amount) => new(amount.IntegerDigitCount, amount.FractionDigitCount);

    /// <inheritdoc />
    public override string ToString() => $"{IntegerDigits} integer and {FractionDigits} fraction digits";
}
