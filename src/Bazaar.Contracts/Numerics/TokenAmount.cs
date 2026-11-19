using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// An exact, non-negative token count of any size, with an optional decimal fraction (doc 48, DECIDED 11.3). It is an
/// arbitrary-size integer (the unscaled value) divided by ten to the power of the scale. It is never negative, NaN or
/// infinite, and arithmetic never wraps.
/// </summary>
/// <remarks>
/// The type has no maximum of its own. Size limits are a <see cref="TokenAmountBounds"/> policy applied where text or JSON
/// is read and in <see cref="TryAdd(TokenAmount, TokenAmount, TokenAmountBounds, out TokenAmount)"/>; the default policy is
/// <see cref="TokenAmountBounds.PostgresNumeric"/>.
/// <para>
/// Values are kept normalised: no trailing zeros after the decimal point, and zero has scale 0. So <c>1.50</c> and
/// <c>1.5</c> are the same value, and equality and hashing are by value. <c>default(TokenAmount)</c> is zero.
/// </para>
/// <para>
/// Text form (<see cref="TryParse(ReadOnlySpan{char}, out TokenAmount)"/>): one or more ASCII digits, then optionally
/// <c>.</c> and one or more ASCII digits, then optionally <c>e</c> or <c>E</c>, an optional <c>+</c> or <c>-</c>, and one
/// or more ASCII digits. This is the JSON number grammar without a sign, except that leading zeros are allowed. So
/// <c>.5</c>, <c>1.</c> and <c>.</c> are refused, as are signs, spaces, separators, NaN, infinity and non-ASCII digits.
/// Length and exponent are checked by arithmetic before any digit is converted.
/// </para>
/// </remarks>
[JsonConverter(typeof(TokenAmountJsonConverter))]
[TypeConverter(typeof(TokenAmountTypeConverter))]
public readonly struct TokenAmount : IEquatable<TokenAmount>, IComparable<TokenAmount>, ISpanParsable<TokenAmount>
{
    private const double _log10Of2 = 0.30102999566398119521373889472449;

    private readonly BigInteger _unscaled;
    private readonly int _scale;
    private readonly int _digits;

    private TokenAmount(BigInteger unscaled, int scale, int digits)
    {
        _unscaled = unscaled;
        _scale = scale;
        _digits = digits;
    }

    /// <summary>Zero tokens.</summary>
    public static TokenAmount Zero => default;

    /// <summary>One token.</summary>
    public static TokenAmount One { get; } = new(BigInteger.One, 0, 1);

    /// <summary>True when the amount is zero.</summary>
    public bool IsZero => _unscaled.IsZero;

    /// <summary>Digits before the decimal point, without leading zeros. Zero for any amount below 1. Constant time.</summary>
    public int IntegerDigitCount => Math.Max(0, _digits - _scale);

    /// <summary>Digits after the decimal point, without trailing zeros. Constant time.</summary>
    public int FractionDigitCount => _scale;

    /// <summary>True when the amount has no more digits on either side of the point than <paramref name="bounds"/> allows.</summary>
    public bool Fits(TokenAmountBounds bounds) =>
        IntegerDigitCount <= bounds.MaxIntegerDigits && FractionDigitCount <= bounds.MaxFractionDigits;

    /// <summary>The amount for a whole token count.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static TokenAmount FromInt64(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value == 0 ? Zero : new TokenAmount(value, 0, DigitCount((ulong)value));
    }

    /// <summary>Exact sum with no size limit. It cannot wrap or fail; use <see cref="TryAdd(TokenAmount, TokenAmount, out TokenAmount)"/> where a bound applies.</summary>
    public static TokenAmount Add(TokenAmount left, TokenAmount right)
    {
        if (left.IsZero)
        {
            return right;
        }

        if (right.IsZero)
        {
            return left;
        }

        var scale = Math.Max(left._scale, right._scale);
        var sum = Align(left, scale) + Align(right, scale);
        return Create(sum, scale);
    }

    /// <inheritdoc cref="Add(TokenAmount, TokenAmount)"/>
    public static TokenAmount operator +(TokenAmount left, TokenAmount right) => Add(left, right);

    /// <summary>Exact sum, refused when it does not fit <see cref="TokenAmountBounds.PostgresNumeric"/>.</summary>
    public static bool TryAdd(TokenAmount left, TokenAmount right, out TokenAmount result) =>
        TryAdd(left, right, TokenAmountBounds.PostgresNumeric, out result);

    /// <summary>
    /// Exact sum, refused (false, <paramref name="result"/> zero) when an operand or the sum does not fit
    /// <paramref name="bounds"/>. Operands are checked first, by digit count, so an oversized operand costs no arithmetic.
    /// </summary>
    public static bool TryAdd(TokenAmount left, TokenAmount right, TokenAmountBounds bounds, out TokenAmount result)
    {
        if (!left.Fits(bounds) || !right.Fits(bounds))
        {
            result = Zero;
            return false;
        }

        var sum = Add(left, right);
        if (!sum.Fits(bounds))
        {
            result = Zero;
            return false;
        }

        result = sum;
        return true;
    }

    /// <summary>
    /// Exact difference <paramref name="left"/> minus <paramref name="right"/>. False (and <paramref name="result"/> zero)
    /// when it would be negative; a negative amount is never produced.
    /// </summary>
    public static bool TrySubtract(TokenAmount left, TokenAmount right, out TokenAmount result)
    {
        if (left < right)
        {
            result = Zero;
            return false;
        }

        if (right.IsZero)
        {
            result = left;
            return true;
        }

        var scale = Math.Max(left._scale, right._scale);
        result = Create(Align(left, scale) - Align(right, scale), scale);
        return true;
    }

    /// <summary>The smaller of two amounts.</summary>
    public static TokenAmount Min(TokenAmount left, TokenAmount right) => left <= right ? left : right;

    /// <summary>The larger of two amounts.</summary>
    public static TokenAmount Max(TokenAmount left, TokenAmount right) => left >= right ? left : right;

    /// <inheritdoc />
    public int CompareTo(TokenAmount other)
    {
        // Digit counts decide most comparisons without arithmetic, so comparing against a huge amount stays cheap.
        var integerDigits = IntegerDigitCount.CompareTo(other.IntegerDigitCount);
        if (integerDigits != 0)
        {
            return integerDigits;
        }

        if (_scale == other._scale)
        {
            return _unscaled.CompareTo(other._unscaled);
        }

        var scale = Math.Max(_scale, other._scale);
        return Align(this, scale).CompareTo(Align(other, scale));
    }

    /// <inheritdoc />
    public bool Equals(TokenAmount other) => _scale == other._scale && _unscaled.Equals(other._unscaled);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TokenAmount other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_unscaled, _scale);

    public static bool operator ==(TokenAmount left, TokenAmount right) => left.Equals(right);

    public static bool operator !=(TokenAmount left, TokenAmount right) => !left.Equals(right);

    public static bool operator <(TokenAmount left, TokenAmount right) => left.CompareTo(right) < 0;

    public static bool operator >(TokenAmount left, TokenAmount right) => left.CompareTo(right) > 0;

    public static bool operator <=(TokenAmount left, TokenAmount right) => left.CompareTo(right) <= 0;

    public static bool operator >=(TokenAmount left, TokenAmount right) => left.CompareTo(right) >= 0;

    /// <summary>The canonical plain decimal: invariant ASCII digits, no exponent, no separators, no trailing fractional zeros.</summary>
    public override string ToString()
    {
        if (_unscaled.IsZero)
        {
            return "0";
        }

        var digits = _unscaled.ToString(CultureInfo.InvariantCulture);
        if (_scale == 0)
        {
            return digits;
        }

        if (digits.Length > _scale)
        {
            return string.Concat(digits.AsSpan(0, digits.Length - _scale), ".", digits.AsSpan(digits.Length - _scale));
        }

        return string.Concat("0.", new string('0', _scale - digits.Length), digits);
    }

    /// <summary>Parses the text form under <see cref="TokenAmountBounds.PostgresNumeric"/>.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out TokenAmount result) =>
        TryParse(text, TokenAmountBounds.PostgresNumeric, out result);

    /// <summary>
    /// Parses the text form (see the type remarks) and refuses a value with more digits than <paramref name="bounds"/>
    /// allows. Text longer than <see cref="TokenAmountBounds.MaxTextLength"/> is refused before it is scanned, and an
    /// exponent is bounded by arithmetic on digit counts before any number is built.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, TokenAmountBounds bounds, out TokenAmount result)
    {
        result = Zero;
        if (text.IsEmpty || text.Length > bounds.MaxTextLength)
        {
            return false;
        }

        var i = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        var integerLength = i;
        if (integerLength == 0)
        {
            return false;
        }

        var fractionLength = 0;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            var fractionStart = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            fractionLength = i - fractionStart;
            if (fractionLength == 0)
            {
                return false;
            }
        }

        long exponent = 0;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            var negative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                negative = text[i] == '-';
                i++;
            }

            var exponentStart = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                exponent = (exponent * 10) + (text[i] - '0');
                if (exponent > bounds.MaxExponentMagnitude)
                {
                    // No value of acceptable length can fit; refuse before reading further.
                    return false;
                }

                i++;
            }

            if (i == exponentStart)
            {
                return false;
            }

            exponent = negative ? -exponent : exponent;
        }

        if (i != text.Length)
        {
            return false;
        }

        // The mantissa digits are text[0..integerLength) followed by the fraction digits after the '.'.
        var mantissaLength = integerLength + fractionLength;
        var first = -1;
        var last = -1;
        for (var k = 0; k < mantissaLength; k++)
        {
            if (MantissaDigit(text, integerLength, k) != '0')
            {
                first = first < 0 ? k : first;
                last = k;
            }
        }

        if (first < 0)
        {
            return true;
        }

        // value = significant digits * 10^shift, where the significant digits run from first to last.
        var significant = last - first + 1;
        var shift = exponent - fractionLength + (mantissaLength - 1 - last);
        int scale;
        long totalDigits;
        if (shift >= 0)
        {
            scale = 0;
            totalDigits = significant + shift;
            if (totalDigits > bounds.MaxIntegerDigits)
            {
                return false;
            }
        }
        else
        {
            if (-shift > bounds.MaxFractionDigits)
            {
                return false;
            }

            scale = (int)-shift;
            totalDigits = significant;
            if (Math.Max(0, totalDigits - scale) > bounds.MaxIntegerDigits)
            {
                return false;
            }
        }

        // Only now, with every size known to fit, are digits converted.
        var buffer = ArrayPool<char>.Shared.Rent(significant);
        try
        {
            for (var k = 0; k < significant; k++)
            {
                buffer[k] = MantissaDigit(text, integerLength, first + k);
            }

            var unscaled = BigInteger.Parse(buffer.AsSpan(0, significant), NumberStyles.None, CultureInfo.InvariantCulture);
            if (shift > 0)
            {
                unscaled *= BigInteger.Pow(10, (int)shift);
            }

            result = new TokenAmount(unscaled, scale, (int)totalDigits);
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>Parses the text form under <see cref="TokenAmountBounds.PostgresNumeric"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid amount within the bounds.</exception>
    public static TokenAmount Parse(ReadOnlySpan<char> text) => Parse(text, TokenAmountBounds.PostgresNumeric);

    /// <summary>Parses the text form under <paramref name="bounds"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid amount within the bounds.</exception>
    public static TokenAmount Parse(ReadOnlySpan<char> text, TokenAmountBounds bounds) =>
        TryParse(text, bounds, out var result)
            ? result
            : throw new FormatException("Not a token amount: expected ASCII digits with an optional fraction and exponent, within the digit bounds.");

    /// <inheritdoc />
    static TokenAmount ISpanParsable<TokenAmount>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc />
    static bool ISpanParsable<TokenAmount>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out TokenAmount result) =>
        TryParse(s, out result);

    /// <inheritdoc />
    static TokenAmount IParsable<TokenAmount>.Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s);
    }

    /// <inheritdoc />
    static bool IParsable<TokenAmount>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TokenAmount result)
    {
        result = Zero;
        return s is not null && TryParse(s, out result);
    }

    private static char MantissaDigit(ReadOnlySpan<char> text, int integerLength, int index) =>
        index < integerLength ? text[index] : text[index + 1];

    /// <summary>The unscaled value rescaled to <paramref name="scale"/>, which is at least the amount's own scale.</summary>
    private static BigInteger Align(TokenAmount amount, int scale) =>
        scale == amount._scale ? amount._unscaled : amount._unscaled * BigInteger.Pow(10, scale - amount._scale);

    /// <summary>Builds a normalised amount: trailing fractional zeros are removed and the digit count is recorded.</summary>
    private static TokenAmount Create(BigInteger unscaled, int scale)
    {
        if (unscaled.IsZero)
        {
            return Zero;
        }

        // Strip trailing zeros in power-of-two steps, so a long run of zeros costs a few divisions, not one per zero.
        for (var step = HighestPowerOfTwoAtMost(scale); step > 0 && scale > 0 && unscaled.IsEven; step >>= 1)
        {
            if (step > scale)
            {
                continue;
            }

            var divisor = BigInteger.Pow(10, step);
            var quotient = BigInteger.DivRem(unscaled, divisor, out var remainder);
            if (remainder.IsZero)
            {
                unscaled = quotient;
                scale -= step;
            }
        }

        return new TokenAmount(unscaled, scale, DigitCount(unscaled));
    }

    private static int HighestPowerOfTwoAtMost(int value) => value <= 0 ? 0 : 1 << (31 - int.LeadingZeroCount(value));

    private static int DigitCount(ulong value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    /// <summary>Decimal digits of a positive value: an estimate from its bit length, corrected by exact comparison.</summary>
    private static int DigitCount(BigInteger value)
    {
        if (value <= ulong.MaxValue)
        {
            return DigitCount((ulong)value);
        }

        var bits = value.GetBitLength();
        var estimate = (int)Math.Floor((bits - 1) * _log10Of2) + 1;
        while (value >= BigInteger.Pow(10, estimate))
        {
            estimate++;
        }

        while (estimate > 1 && value < BigInteger.Pow(10, estimate - 1))
        {
            estimate--;
        }

        return estimate;
    }
}
