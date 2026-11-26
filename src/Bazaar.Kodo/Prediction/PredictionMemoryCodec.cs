using System.Text.Json;

namespace Bazaar.Kodo.Prediction;

/// <summary>Reads and writes <see cref="PredictionMemory"/> as UTF-8 JSON. Parsing never throws on bad input.</summary>
public static class PredictionMemoryCodec
{
    /// <summary>Input larger than this is refused before parsing. PROPOSED.</summary>
    public const int MaxBytes = 1024 * 1024;

    public static byte[] ToUtf8Bytes(PredictionMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return JsonSerializer.SerializeToUtf8Bytes(memory, PredictionJsonContext.Default.PredictionMemory);
    }

    /// <summary>Parses saved memory. Returns false for oversized, malformed, wrong-version or unknown-member input.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out PredictionMemory? memory)
    {
        memory = null;
        if (utf8.Length == 0 || utf8.Length > MaxBytes)
        {
            return false;
        }

        try
        {
            PredictionMemory? parsed = JsonSerializer.Deserialize(utf8, PredictionJsonContext.Default.PredictionMemory);
            if (parsed is null || parsed.Version != PredictionMemory.CurrentVersion)
            {
                return false;
            }

            memory = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
