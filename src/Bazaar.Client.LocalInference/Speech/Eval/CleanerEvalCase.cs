using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.LocalInference.Speech.Eval;

/// <summary>One eval item: a rambling dictation transcript and the cleaned text a reviewer expects (doc 29, Testing: cleaner eval).</summary>
public sealed record CleanerEvalCase(string Id, string Input, string Expected);

/// <summary>Parses the cleaner eval corpus. The corpus holds no personal data; the shipped file is original sample text.</summary>
public static class CleanerEvalCorpus
{
    /// <summary>Parses a JSON array of cases. Throws <see cref="InvalidDataException"/> on an empty corpus, a missing or blank field, or a duplicate id.</summary>
    public static IReadOnlyList<CleanerEvalCase> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var cases = JsonSerializer.Deserialize(json, CleanerEvalJsonContext.Default.ListCleanerEvalCase)
            ?? throw new InvalidDataException("The cleaner eval corpus is empty.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Input) || string.IsNullOrWhiteSpace(item.Expected))
            {
                throw new InvalidDataException("A cleaner eval case has an empty id, input or expected text.");
            }

            if (!ids.Add(item.Id))
            {
                throw new InvalidDataException($"Cleaner eval case id '{item.Id}' is duplicated.");
            }
        }

        return cases;
    }
}

/// <summary>Source-generated JSON for the cleaner eval corpus (camelCase, no reflection serializer).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<CleanerEvalCase>))]
internal sealed partial class CleanerEvalJsonContext : JsonSerializerContext;
