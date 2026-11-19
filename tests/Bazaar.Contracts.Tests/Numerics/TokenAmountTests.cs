using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Contracts.Tests.Numerics;

/// <summary>
/// TokenAmount (doc 48, DECIDED 11.3): exact, non-negative, any size, never wraps. Property tests run against a reference of
/// a BigInteger numerator over a power of ten, with a fixed seed so every run checks the same cases.
/// </summary>
public class TokenAmountTests
{
    private const int _seed = 20261119;
    private const int _cases = 2000;

    private static readonly TokenAmountBounds _pg = TokenAmountBounds.PostgresNumeric;

    [Fact]
    public void Postgres_numeric_bounds_are_the_documented_limits()
    {
        Assert.Equal(131_072, _pg.MaxIntegerDigits);
        Assert.Equal(16_383, _pg.MaxFractionDigits);
    }

    [Theory]
    [InlineData("131072,16383", 131_072, 16_383)]
    [InlineData(" 5 , 0 ", 5, 0)]
    [InlineData("0,0", 0, 0)]
    public void Bounds_text_form_round_trips(string text, int before, int after)
    {
        var bounds = TokenAmountBounds.Parse(text);

        Assert.Equal(new TokenAmountBounds(before, after), bounds);
        Assert.Equal(bounds, TokenAmountBounds.Parse(bounds.ToString()));
        Assert.Equal("131072,16383", _pg.ToString());
    }

    [Theory]
    [InlineData("unlimited")]
    [InlineData("UNLIMITED")]
    public void Unlimited_bounds_parse_and_print_as_unlimited(string text)
    {
        var bounds = TokenAmountBounds.Parse(text);

        Assert.True(bounds.IsUnlimited);
        Assert.Equal("unlimited", bounds.ToString());
        Assert.Equal(new TokenAmountBounds(10, 3), TokenAmountBounds.Intersect(bounds, new TokenAmountBounds(10, 3)));
        Assert.False(_pg.IsUnlimited);
    }

    [Theory]
    [InlineData("")]
    [InlineData("131072")]
    [InlineData("-1,0")]
    [InlineData("1,-1")]
    [InlineData("1.5,2")]
    [InlineData("a,b")]
    [InlineData("unlimited,1")]
    public void Bounds_text_form_refuses_anything_else(string text)
    {
        Assert.False(TokenAmountBounds.TryParse(text, out _));
        Assert.Throws<FormatException>(() => TokenAmountBounds.Parse(text));
    }

    [Fact]
    public void Bounds_refuse_negative_digit_counts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenAmountBounds(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenAmountBounds(0, -1));
    }

    [Fact]
    public void Zero_one_and_default_behave()
    {
        Assert.Equal(TokenAmount.Zero, default);
        Assert.True(TokenAmount.Zero.IsZero);
        Assert.Equal("0", TokenAmount.Zero.ToString());
        Assert.Equal("1", TokenAmount.One.ToString());
        Assert.Equal(0, TokenAmount.Zero.IntegerDigitCount);
        Assert.Equal(1, TokenAmount.One.IntegerDigitCount);
        Assert.Equal(TokenAmount.One, TokenAmount.FromInt64(1));
    }

    [Fact]
    public void FromInt64_refuses_negative_values_and_takes_long_max()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenAmount.FromInt64(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenAmount.FromInt64(long.MinValue));
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), TokenAmount.FromInt64(long.MaxValue).ToString());
        Assert.Equal(TokenAmount.Zero, TokenAmount.FromInt64(0));
    }

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("1.500000", "1.5")]
    [InlineData("0001.5", "1.5")]
    [InlineData("0.0", "0")]
    [InlineData("000", "0")]
    [InlineData("10", "10")]
    [InlineData("1e3", "1000")]
    [InlineData("1E3", "1000")]
    [InlineData("1e+3", "1000")]
    [InlineData("1000e-3", "1")]
    [InlineData("15e-1", "1.5")]
    [InlineData("0.000123", "0.000123")]
    [InlineData("1.23e-5", "0.0000123")]
    [InlineData("0e999", "0")]
    [InlineData("12.5e1", "125")]
    public void Parse_normalises_to_the_canonical_decimal(string text, string canonical)
    {
        var amount = TokenAmount.Parse(text);

        Assert.Equal(canonical, amount.ToString());
        Assert.Equal(TokenAmount.Parse(canonical), amount);
        Assert.Equal(TokenAmount.Parse(canonical).GetHashCode(), amount.GetHashCode());
    }

    // Decided: ".5", "1." and "." are refused (the JSON number grammar needs a digit on both sides of the point). Leading
    // zeros are allowed. Signs, spaces, separators, NaN, infinity and non-ASCII digits are refused.
    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("-0")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1_000")]
    [InlineData("1,000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("∞")]
    [InlineData("٣")]
    [InlineData("１")]
    [InlineData(".")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("e5")]
    [InlineData("1.2.3")]
    [InlineData("1e5e5")]
    [InlineData("0x10")]
    [InlineData("1e999999999")]
    [InlineData("1e-999999999")]
    [InlineData("1e99999999999999999999999999")]
    [InlineData("0e999999999")]
    public void Hostile_and_malformed_text_is_refused(string text)
    {
        Assert.False(TokenAmount.TryParse(text, out var amount));
        Assert.Equal(TokenAmount.Zero, amount);
        Assert.Throws<FormatException>(() => TokenAmount.Parse(text));
    }

    [Fact]
    public void Exactly_the_integer_digit_bound_is_accepted_and_one_more_is_refused()
    {
        var atBound = new string('9', _pg.MaxIntegerDigits);

        Assert.True(TokenAmount.TryParse(atBound, out var amount));
        Assert.Equal(_pg.MaxIntegerDigits, amount.IntegerDigitCount);
        Assert.Equal(atBound, amount.ToString());
        Assert.False(TokenAmount.TryParse("1" + atBound, out _));

        Assert.True(TokenAmount.TryParse($"1e{_pg.MaxIntegerDigits - 1}", out var power));
        Assert.Equal(_pg.MaxIntegerDigits, power.IntegerDigitCount);
        Assert.False(TokenAmount.TryParse($"1e{_pg.MaxIntegerDigits}", out _));
    }

    [Fact]
    public void Exactly_the_fraction_digit_bound_is_accepted_and_one_more_is_refused()
    {
        var atBound = "0." + new string('0', _pg.MaxFractionDigits - 1) + "1";

        Assert.True(TokenAmount.TryParse(atBound, out var amount));
        Assert.Equal(_pg.MaxFractionDigits, amount.FractionDigitCount);
        Assert.Equal(atBound, amount.ToString());
        Assert.False(TokenAmount.TryParse("0." + new string('0', _pg.MaxFractionDigits) + "1", out _));

        Assert.True(TokenAmount.TryParse($"1e-{_pg.MaxFractionDigits}", out var power));
        Assert.Equal(_pg.MaxFractionDigits, power.FractionDigitCount);
        Assert.False(TokenAmount.TryParse($"1e-{_pg.MaxFractionDigits + 1}", out _));
    }

    [Fact]
    public void The_largest_numeric_value_round_trips()
    {
        var text = new string('9', _pg.MaxIntegerDigits) + "." + new string('9', _pg.MaxFractionDigits);

        var amount = TokenAmount.Parse(text);

        Assert.Equal(text, amount.ToString());
        Assert.True(amount.Fits(_pg));
        Assert.False(TokenAmount.TryAdd(amount, TokenAmount.Parse("0." + new string('0', _pg.MaxFractionDigits - 1) + "1"), out _));
    }

    [Fact]
    public void A_five_million_digit_string_is_refused_quickly()
    {
        var text = new string('7', 5_000_000);

        var clock = Stopwatch.StartNew();
        var parsed = TokenAmount.TryParse(text, out _);
        clock.Stop();

        Assert.False(parsed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void Smaller_bounds_apply_when_given()
    {
        var bounds = new TokenAmountBounds(3, 2);

        Assert.True(TokenAmount.TryParse("999.99", bounds, out _));
        Assert.False(TokenAmount.TryParse("1000", bounds, out _));
        Assert.False(TokenAmount.TryParse("0.001", bounds, out _));
        Assert.False(TokenAmount.TryParse("1e3", bounds, out _));
        Assert.True(TokenAmount.TryParse("1e2", bounds, out _));
        Assert.False(TokenAmount.TryAdd(TokenAmount.Parse("999"), TokenAmount.One, bounds, out var refused));
        Assert.Equal(TokenAmount.Zero, refused);
        Assert.True(TokenAmount.TryAdd(TokenAmount.Parse("998"), TokenAmount.One, bounds, out var sum));
        Assert.Equal(TokenAmount.Parse("999"), sum);
    }

    [Fact]
    public void Unbounded_addition_goes_past_any_bound_without_wrapping()
    {
        var max = TokenAmount.Parse(new string('9', _pg.MaxIntegerDigits));

        var sum = max + TokenAmount.One;

        Assert.Equal(_pg.MaxIntegerDigits + 1, sum.IntegerDigitCount);
        Assert.False(sum.Fits(_pg));
        Assert.True(sum > max);
        Assert.False(TokenAmount.TryAdd(max, TokenAmount.One, out _));
    }

    [Fact]
    public void Sums_beyond_long_are_exact()
    {
        var sum = TokenAmount.FromInt64(long.MaxValue) + TokenAmount.FromInt64(long.MaxValue);

        Assert.Equal((new BigInteger(long.MaxValue) * 2).ToString(CultureInfo.InvariantCulture), sum.ToString());
    }

    [Fact]
    public void Halves_add_to_exactly_one()
    {
        var half = TokenAmount.Parse("0.5");

        Assert.Equal(TokenAmount.One, half + half);
        Assert.Equal("1", (half + half).ToString());
        Assert.True(TokenAmount.TrySubtract(TokenAmount.One, half, out var rest));
        Assert.Equal(half, rest);
    }

    [Fact]
    public void TrySubtract_never_returns_a_negative_amount()
    {
        Assert.False(TokenAmount.TrySubtract(TokenAmount.Parse("1.5"), TokenAmount.Parse("1.50001"), out var result));
        Assert.Equal(TokenAmount.Zero, result);
        Assert.True(TokenAmount.TrySubtract(TokenAmount.Parse("1.5"), TokenAmount.Parse("1.5"), out var zero));
        Assert.True(zero.IsZero);
    }

    [Fact]
    public void Min_and_max_pick_by_value()
    {
        var a = TokenAmount.Parse("2.5");
        var b = TokenAmount.Parse("10");

        Assert.Equal(a, TokenAmount.Min(a, b));
        Assert.Equal(b, TokenAmount.Max(a, b));
        Assert.Equal(a, TokenAmount.Min(b, a));
    }

    [Fact]
    public void Property_parse_format_round_trip_matches_the_reference()
    {
        var random = new Random(_seed);
        for (var i = 0; i < _cases; i++)
        {
            var (text, reference) = RandomText(random);

            var amount = TokenAmount.Parse(text);

            Assert.Equal(reference.Canonical(), amount.ToString());
            Assert.Equal(amount, TokenAmount.Parse(amount.ToString()));
        }
    }

    [Fact]
    public void Property_ordering_is_total_and_consistent_with_equality()
    {
        var random = new Random(_seed + 1);
        for (var i = 0; i < _cases; i++)
        {
            var (leftText, left) = RandomText(random);
            var (rightText, right) = RandomText(random);
            // One case in eight compares a value with itself written differently (leading zeros), so equality is exercised.
            var same = random.Next(8) == 0;
            var a = TokenAmount.Parse(leftText);
            var b = same ? TokenAmount.Parse("00" + leftText) : TokenAmount.Parse(rightText);
            var expected = same ? 0 : left.CompareTo(right);

            var actual = Math.Sign(a.CompareTo(b));

            Assert.Equal(expected, actual);
            Assert.Equal(actual == 0, a == b);
            Assert.Equal(actual == 0, a.Equals(b));
            Assert.Equal(-actual, Math.Sign(b.CompareTo(a)));
            Assert.Equal(actual < 0, a < b);
            Assert.Equal(actual >= 0, a >= b);
            if (actual == 0)
            {
                Assert.Equal(a.GetHashCode(), b.GetHashCode());
            }
        }
    }

    [Fact]
    public void Property_add_and_subtract_match_the_reference()
    {
        var random = new Random(_seed + 2);
        for (var i = 0; i < _cases; i++)
        {
            var (leftText, left) = RandomText(random);
            var (rightText, right) = RandomText(random);
            var a = TokenAmount.Parse(leftText);
            var b = TokenAmount.Parse(rightText);

            Assert.Equal(left.Add(right).Canonical(), (a + b).ToString());
            Assert.True(TokenAmount.TryAdd(a, b, out var sum));
            Assert.Equal(a + b, sum);

            var subtracted = TokenAmount.TrySubtract(a, b, out var difference);
            Assert.Equal(left.CompareTo(right) >= 0, subtracted);
            Assert.Equal(subtracted ? left.Subtract(right).Canonical() : "0", difference.ToString());
            if (subtracted)
            {
                Assert.Equal(a, difference + b);
            }
        }
    }

    [Fact]
    public void Digit_counts_match_the_canonical_text()
    {
        var random = new Random(_seed + 3);
        for (var i = 0; i < _cases; i++)
        {
            var (text, _) = RandomText(random);
            var amount = TokenAmount.Parse(text);
            var canonical = amount.ToString();
            var point = canonical.IndexOf('.', StringComparison.Ordinal);
            var integer = point < 0 ? canonical : canonical[..point];

            Assert.Equal(integer == "0" ? 0 : integer.Length, amount.IntegerDigitCount);
            Assert.Equal(point < 0 ? 0 : canonical.Length - point - 1, amount.FractionDigitCount);
        }
    }

    [Fact]
    public void Text_conversion_works_through_the_type_converter()
    {
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(typeof(TokenAmount));

        Assert.Equal(TokenAmount.Parse("2.5e6"), converter.ConvertFromInvariantString("2.5e6"));
        Assert.Throws<FormatException>(() => converter.ConvertFromInvariantString("-5"));
    }

    /// <summary>Random amount text with leading zeros, trailing fractional zeros and exponents, plus its reference value.</summary>
    private static (string Text, Reference Value) RandomText(Random random)
    {
        var builder = new StringBuilder();
        var integerLength = random.Next(1, random.Next(2) == 0 ? 4 : 45);
        for (var i = 0; i < integerLength; i++)
        {
            builder.Append((char)('0' + random.Next(10)));
        }

        var fraction = string.Empty;
        if (random.Next(2) == 0)
        {
            var fractionLength = random.Next(1, 25);
            var digits = new StringBuilder();
            for (var i = 0; i < fractionLength; i++)
            {
                digits.Append((char)('0' + random.Next(10)));
            }

            fraction = digits.ToString();
            builder.Append('.').Append(fraction);
        }

        var exponent = 0;
        if (random.Next(4) == 0)
        {
            exponent = random.Next(-30, 31);
            builder.Append(random.Next(2) == 0 ? 'e' : 'E').Append(exponent.ToString(CultureInfo.InvariantCulture));
        }

        var mantissa = BigInteger.Parse(builder.ToString(0, integerLength) + fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        var scale = fraction.Length - exponent;
        var value = scale >= 0 ? new Reference(mantissa, scale) : new Reference(mantissa * BigInteger.Pow(10, -scale), 0);
        return (builder.ToString(), value);
    }

    /// <summary>The reference model: <c>Numerator / 10^Scale</c>, with plain BigInteger arithmetic.</summary>
    private readonly record struct Reference(BigInteger Numerator, int Scale)
    {
        public Reference Add(Reference other)
        {
            var scale = Math.Max(Scale, other.Scale);
            return new Reference(Up(scale) + other.Up(scale), scale);
        }

        public Reference Subtract(Reference other)
        {
            var scale = Math.Max(Scale, other.Scale);
            return new Reference(Up(scale) - other.Up(scale), scale);
        }

        public int CompareTo(Reference other)
        {
            var scale = Math.Max(Scale, other.Scale);
            return Math.Sign(Up(scale).CompareTo(other.Up(scale)));
        }

        public string Canonical()
        {
            var numerator = Numerator;
            var scale = Scale;
            while (scale > 0 && numerator % 10 == 0)
            {
                numerator /= 10;
                scale--;
            }

            if (numerator.IsZero)
            {
                return "0";
            }

            var digits = numerator.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
            return scale == 0 ? digits : digits[..^scale] + "." + digits[^scale..];
        }

        private BigInteger Up(int scale) => Numerator * BigInteger.Pow(10, scale - Scale);
    }
}
