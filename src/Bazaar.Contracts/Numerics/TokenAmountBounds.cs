using System.Globalization;

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

    /// <summary>
    /// No digit limit of its own: the most digits an in-memory amount can index on each side. Text form <c>unlimited</c>. A
    /// parse into <see cref="TokenAmount"/> is still bounded by the process, so callers that parse untrusted text intersect
    /// this with an in-memory maximum (doc 48, DECIDED 11.4, "Intake guard").
    /// </summary>
    public static TokenAmountBounds Unlimited { get; } = new(int.MaxValue, int.MaxValue);

    /// <summary>The text form of <see cref="Unlimited"/>.</summary>
    public const string UnlimitedText = "unlimited";

    /// <summary>True for <see cref="Unlimited"/>.</summary>
    public bool IsUnlimited => this == Unlimited;

    /// <summary>The tighter of two bounds on each side.</summary>
    public static TokenAmountBounds Intersect(TokenAmountBounds left, TokenAmountBounds right) =>
        new(Math.Min(left.MaxIntegerDigits, right.MaxIntegerDigits), Math.Min(left.MaxFractionDigits, right.MaxFractionDigits));

    /// <summary>
    /// The text form: <c>unlimited</c>, or the integer and fraction digit counts separated by a comma, such as
    /// <c>131072,16383</c>.
    /// </summary>
    public override string ToString() =>
        IsUnlimited ? UnlimitedText : string.Create(CultureInfo.InvariantCulture, $"{MaxIntegerDigits},{MaxFractionDigits}");

    /// <summary>Parses the text form of <see cref="ToString"/>: <c>unlimited</c> (any case) or two non-negative counts.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out TokenAmountBounds bounds)
    {
        bounds = default;
        text = text.Trim();
        if (text.Equals(UnlimitedText, StringComparison.OrdinalIgnoreCase))
        {
            bounds = Unlimited;
            return true;
        }

        var comma = text.IndexOf(',');
        if (comma < 0
            || !int.TryParse(text[..comma].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var integer)
            || !int.TryParse(text[(comma + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var fraction))
        {
            return false;
        }

        bounds = new TokenAmountBounds(integer, fraction);
        return true;
    }

    /// <summary>Parses the text form of <see cref="ToString"/>.</summary>
    /// <exception cref="FormatException">The text is not <c>unlimited</c> or two non-negative counts.</exception>
    public static TokenAmountBounds Parse(ReadOnlySpan<char> text) =>
        TryParse(text, out var bounds)
            ? bounds
            : throw new FormatException("Not digit bounds: expected 'unlimited' or '<integer digits>,<fraction digits>'.");

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
