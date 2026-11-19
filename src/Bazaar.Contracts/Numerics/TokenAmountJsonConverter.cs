using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// JSON for <see cref="TokenAmount"/> (doc 48, DECIDED 11.3). Writes a JSON string holding the canonical decimal. Reads a
/// JSON string or a JSON number; a number is read from its raw text, never through <see cref="double"/>. Both are held to
/// <see cref="Bounds"/>, and over-long input is refused by its length before any digit is converted.
/// </summary>
/// <remarks>Registered on the type, so it also applies under source-generated contexts.</remarks>
public sealed class TokenAmountJsonConverter : JsonConverter<TokenAmount>
{
    /// <summary>Uses <see cref="TokenAmountBounds.PostgresNumeric"/>.</summary>
    public TokenAmountJsonConverter()
        : this(TokenAmountBounds.PostgresNumeric)
    {
    }

    /// <summary>Uses the given bounds.</summary>
    public TokenAmountJsonConverter(TokenAmountBounds bounds) => Bounds = bounds;

    /// <summary>The size policy applied when reading.</summary>
    public TokenAmountBounds Bounds { get; }

    /// <inheritdoc />
    public override TokenAmount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number))
        {
            throw new JsonException("A token amount must be a JSON string or number.");
        }

        if (reader.TokenType == JsonTokenType.String && reader.ValueIsEscaped)
        {
            // Escapes are rare and only make the raw text longer; the unescaped text is held to the same length bound.
            return ParseOrThrow(reader.GetString().AsSpan());
        }

        var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (length > Bounds.MaxTextLength)
        {
            throw new JsonException("The token amount is longer than the digit bounds allow.");
        }

        var count = (int)length;
        var chars = ArrayPool<char>.Shared.Rent(Math.Max(count, 1));
        try
        {
            var written = 0;
            if (reader.HasValueSequence)
            {
                foreach (var segment in reader.ValueSequence)
                {
                    if (!TryCopyAscii(segment.Span, chars.AsSpan(written)))
                    {
                        throw new JsonException("A token amount holds ASCII digits only.");
                    }

                    written += segment.Length;
                }
            }
            else if (!TryCopyAscii(reader.ValueSpan, chars))
            {
                throw new JsonException("A token amount holds ASCII digits only.");
            }

            return ParseOrThrow(chars.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TokenAmount value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }

    private TokenAmount ParseOrThrow(ReadOnlySpan<char> text) =>
        TokenAmount.TryParse(text, Bounds, out var amount)
            ? amount
            : throw new JsonException("Not a token amount: expected ASCII digits with an optional fraction and exponent, within the digit bounds.");

    private static bool TryCopyAscii(ReadOnlySpan<byte> source, Span<char> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] > 0x7F)
            {
                return false;
            }

            destination[i] = (char)source[i];
        }

        return true;
    }
}
