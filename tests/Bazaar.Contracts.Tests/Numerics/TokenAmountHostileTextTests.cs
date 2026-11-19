using System.Diagnostics;
using System.Text.Json;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Contracts.Tests.Numerics;

/// <summary>Chaos round 3: hostile amount text. Parsing answers false or a value, quickly; the JSON converter throws only JsonException.</summary>
public class TokenAmountHostileTextTests
{
    private static readonly JsonSerializerOptions _json = new() { Converters = { new TokenAmountJsonConverter() } };

    [Theory]
    [InlineData("1e2147483647")]
    [InlineData("1e2147483648")]
    [InlineData("1e-2147483648")]
    [InlineData("1e99999999999999999999999")]
    [InlineData("1e-99999999999999999999999")]
    [InlineData("0e2147483647")]
    [InlineData("0.000e2147483647")]
    [InlineData("9e+9223372036854775807")]
    [InlineData("1e1e1")]
    [InlineData("1e")]
    [InlineData("e1")]
    [InlineData(".e1")]
    [InlineData("1.e1")]
    [InlineData("-0")]
    [InlineData("+1")]
    [InlineData("\u0661\u0662")]
    [InlineData("1\u200b2")]
    [InlineData("1 2")]
    [InlineData("1_000")]
    [InlineData("1,000")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1\0")]
    public void Hostile_amount_text_is_answered_quickly_without_throwing(string text)
    {
        var watch = Stopwatch.StartNew();

        var ok = TokenAmount.TryParse(text, out var amount);

        Assert.True(watch.ElapsedMilliseconds < 2000, $"'{text}' took {watch.ElapsedMilliseconds} ms");
        if (ok)
            Assert.True(TokenAmount.TryParse(amount.ToString(), out var again) && again.Equals(amount));
    }

    [Theory]
    [InlineData("1e2147483647")]
    [InlineData("\"1e99999999999999999999999\"")]
    [InlineData("-1")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"\\u0000\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("1.5e")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890e99999")]
    public void Malformed_json_amounts_throw_only_json_exception(string json)
    {
        try
        {
            _ = JsonSerializer.Deserialize<TokenAmount>(json, _json);
        }
        catch (JsonException)
        {
            // Designed refusal.
        }
    }

    [Fact]
    public void Random_text_never_throws_from_try_parse()
    {
        const string Alphabet = "0123456789.e+-E \u0661\u200b\0_,xN";
        var random = new Random(48);
        for (var i = 0; i < 20000; i++)
        {
            var text = string.Create(random.Next(0, 24), random, static (span, r) =>
            {
                for (var j = 0; j < span.Length; j++)
                    span[j] = Alphabet[r.Next(Alphabet.Length)];
            });

            _ = TokenAmount.TryParse(text, out _);
        }
    }
}
