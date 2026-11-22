using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Contracts.Tests.Numerics;

/// <summary>A wire record holding amounts, for the source-generated round trip.</summary>
public sealed record TokenAmountHolder(TokenAmount Tokens, TokenAmount? Optional);

[JsonSerializable(typeof(TokenAmountHolder))]
[JsonSerializable(typeof(TokenAmount))]
internal sealed partial class TokenAmountTestJsonContext : JsonSerializerContext;

/// <summary>JSON for TokenAmount (doc 48, DECIDED 11.3): a string out; a string or a raw number in, never through double.</summary>
public class TokenAmountJsonTests
{
    private static readonly JsonSerializerOptions _reflection = new();
    private static readonly JsonSerializerOptions _smallBuffer = new(TokenAmountTestJsonContext.Default.Options) { DefaultBufferSize = 16 };
    private static readonly JsonSerializerOptions _threeDigits = new() { Converters = { new TokenAmountJsonConverter(new TokenAmountBounds(3, 0)) } };

    [Fact]
    public void Writes_a_json_string_of_the_canonical_decimal()
    {
        var json = JsonSerializer.Serialize(TokenAmount.Parse("1.50"), TokenAmountTestJsonContext.Default.TokenAmount);

        Assert.Equal("\"1.5\"", json);
    }

    [Theory]
    [InlineData("\"1e3\"", "1000")]
    [InlineData("1e3", "1000")]
    [InlineData("1E+3", "1000")]
    [InlineData("0.5", "0.5")]
    [InlineData("1234567890123456789012345678901234567890", "1234567890123456789012345678901234567890")]
    [InlineData("\"1234567890123456789012345678901234567890\"", "1234567890123456789012345678901234567890")]
    [InlineData("12345678901234567890123456789.000000000000000000001", "12345678901234567890123456789.000000000000000000001")]
    [InlineData("\"\\u0031\\u0035\"", "15")]
    public void Reads_strings_and_raw_number_tokens_exactly(string json, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize(json, TokenAmountTestJsonContext.Default.TokenAmount).ToString());
        Assert.Equal(expected, JsonSerializer.Deserialize<TokenAmount>(json, _reflection).ToString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"-1\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"\"")]
    [InlineData("\"٣\"")]
    [InlineData("1e999999999")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Refuses_invalid_amounts(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, TokenAmountTestJsonContext.Default.TokenAmount));
    }

    [Fact]
    public void Refuses_an_over_long_number_by_length()
    {
        var json = new string('9', 5_000_000);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, TokenAmountTestJsonContext.Default.TokenAmount));
    }

    [Fact]
    public void Round_trips_a_record_through_the_source_generated_context()
    {
        var holder = new TokenAmountHolder(TokenAmount.Parse("1234567890123456789012345678901234567890.25"), null);

        var json = JsonSerializer.Serialize(holder, TokenAmountTestJsonContext.Default.TokenAmountHolder);
        var back = JsonSerializer.Deserialize(json, TokenAmountTestJsonContext.Default.TokenAmountHolder);

        Assert.Equal("{\"Tokens\":\"1234567890123456789012345678901234567890.25\",\"Optional\":null}", json);
        Assert.Equal(holder, back);
    }

    [Fact]
    public void Reads_a_long_number_from_a_stream_with_a_small_buffer()
    {
        // The stream reader starts with a 16-byte buffer and must grow it to hold the 70,000-digit token.
        var json = "{\"Tokens\":" + new string('8', 70_000) + ",\"Optional\":2}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var back = JsonSerializer.Deserialize<TokenAmountHolder>(stream, _smallBuffer);

        Assert.NotNull(back);
        Assert.Equal(new string('8', 70_000), back.Tokens.ToString());
        Assert.Equal(TokenAmount.FromInt64(2), back.Optional);
    }

    [Theory]
    [InlineData("123456789012345678901234567890.5")]
    [InlineData("\"123456789012345678901234567890.5\"")]
    public void Reads_a_value_that_spans_several_sequence_segments(string json)
    {
        var sequence = Segments(Encoding.UTF8.GetBytes(json), 4);
        var reader = new Utf8JsonReader(sequence);
        Assert.True(reader.Read());
        Assert.True(reader.HasValueSequence);

        var amount = new TokenAmountJsonConverter().Read(ref reader, typeof(TokenAmount), _reflection);

        Assert.Equal("123456789012345678901234567890.5", amount.ToString());
    }

    private static System.Buffers.ReadOnlySequence<byte> Segments(byte[] bytes, int size)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var next = new Segment(bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset)), offset);
            first ??= next;
            last?.Link(next);
            last = next;
        }

        return new System.Buffers.ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : System.Buffers.ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void Link(Segment next) => Next = next;
    }

    [Fact]
    public void A_converter_with_smaller_bounds_refuses_what_the_default_accepts()
    {
        var options = _threeDigits;

        Assert.Equal("999", JsonSerializer.Deserialize<TokenAmount>("999", options).ToString());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TokenAmount>("1000", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TokenAmount>("\"0.5\"", options));
    }
}
