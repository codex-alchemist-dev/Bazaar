using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Graph;

/// <summary>
/// Writes <see cref="ReadOnlyMemory{T}"/> of float as a plain JSON number array, since System.Text.Json has no built-in
/// converter for it. The wire form is the same as <c>float[]</c>, so a non-.NET client reads an ordinary array. The reader
/// is hand-written, so the converter needs no reflection and stays AOT-safe.
/// </summary>
internal sealed class PackedFloatsJsonConverter : JsonConverter<ReadOnlyMemory<float>>
{
    public override ReadOnlyMemory<float> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array of numbers.");

        var values = new List<float>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return values.ToArray();
            values.Add(reader.GetSingle());
        }

        throw new JsonException("The number array is not terminated.");
    }

    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<float> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var number in value.Span)
            writer.WriteNumberValue(number);
        writer.WriteEndArray();
    }
}
