using System.Text.Json;

namespace Bazaar.Contracts.Wire.Codec;

/// <summary>
/// Walks a JSON document once, without building anything, and rejects it when it nests deeper, holds a longer string or
/// holds a larger collection than <see cref="WireDecodeLimits"/> allows (doc 90 section 4.5). System.Text.Json has no
/// string or collection caps of its own.
/// </summary>
internal static class JsonLimitScanner
{
    /// <summary>Throws <see cref="WireFormatException"/> when <paramref name="json"/> breaks a limit or is not well formed.</summary>
    public static void Check(ReadOnlySpan<byte> json, WireDecodeLimits limits)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = limits.MaxDepth });
        var counts = new Stack<(int Count, bool IsArray)>();
        var current = (Count: 0, IsArray: false);
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (current.IsArray) CountItem(ref current, limits);
                        counts.Push(current);
                        current = (0, reader.TokenType == JsonTokenType.StartArray);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        current = counts.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        CountItem(ref current, limits);
                        CheckString(reader, limits);
                        break;
                    case JsonTokenType.String:
                        if (current.IsArray) CountItem(ref current, limits);
                        CheckString(reader, limits);
                        break;
                    default:
                        if (current.IsArray) CountItem(ref current, limits);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new WireFormatException("Frame is not valid JSON or nests too deeply.", ex);
        }
    }

    private static void CountItem(ref (int Count, bool IsArray) container, WireDecodeLimits limits)
    {
        if (++container.Count > limits.MaxCollectionItems) throw new WireFormatException("A collection is longer than the decode limit.");
    }

    private static void CheckString(Utf8JsonReader reader, WireDecodeLimits limits)
    {
        var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (length > limits.MaxStringBytes) throw new WireFormatException("A string is longer than the decode limit.");
    }
}
