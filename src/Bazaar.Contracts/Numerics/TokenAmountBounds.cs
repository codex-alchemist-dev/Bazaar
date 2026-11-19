namespace Bazaar.Contracts.Numerics;

/// <summary>
/// A size policy for <see cref="TokenAmount"/>: how many digits a value may have before and after the decimal point. The
/// amount itself has no maximum; a bound applies where a value is parsed, read from JSON, summed with
/// <see cref="TokenAmount.TryAdd(TokenAmount, TokenAmount, TokenAmountBounds, out TokenAmount)"/>, or stored (doc 48, DECIDED 11.3).
/// </summary>
public readonly record struct TokenAmountBounds
{
    /// <summary>
    /// The PostgreSQL <c>numeric</c> limits: "up to 131072 digits before the decimal point; up to 16383 digits after the
    /// decimal point" (PostgreSQL documentation, section 8.1 "Numeric Types", table 8.2). Values within these bounds fit
    /// an unconstrained <c>numeric</c> column. The default bound everywhere a bound is not given.
    /// </summary>
    public static TokenAmountBounds PostgresNumeric { get; } = new(131_072, 16_383);

    /// <param name="maxIntegerDigits">Most digits allowed before the decimal point. Zero or more.</param>
    /// <param name="maxFractionDigits">Most digits allowed after the decimal point. Zero or more.</param>
    /// <exception cref="ArgumentOutOfRangeException">A digit count is negative.</exception>
    public TokenAmountBounds(int maxIntegerDigits, int maxFractionDigits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxIntegerDigits);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFractionDigits);
        MaxIntegerDigits = maxIntegerDigits;
        MaxFractionDigits = maxFractionDigits;
    }

    /// <summary>Most digits allowed before the decimal point.</summary>
    public int MaxIntegerDigits { get; }

    /// <summary>Most digits allowed after the decimal point.</summary>
    public int MaxFractionDigits { get; }

    /// <summary>Room for an exponent part in text: <c>e</c>, a sign and up to 20 digits.</summary>
    private const long _exponentTextAllowance = 22;

    /// <summary>
    /// The longest text a parse accepts under these bounds: every allowed digit, one <c>.</c> and an exponent part. Longer
    /// text is refused before any digit is converted, so leading zeros past this length are refused too.
    /// </summary>
    public long MaxTextLength => (long)MaxIntegerDigits + MaxFractionDigits + 1 + _exponentTextAllowance;

    /// <summary>
    /// The largest exponent magnitude worth reading. Beyond it no non-zero value of acceptable length can fit, so the parse
    /// stops reading digits there and refuses.
    /// </summary>
    internal long MaxExponentMagnitude => MaxTextLength + MaxIntegerDigits + MaxFractionDigits;
}
