using System.Text.Json;

namespace Bazaar.Client.LocalInference.Benchmark;

/// <summary>One dictation-style transcript for the voice cleanup benchmark. <c>MustKeep</c> lists the numbers, names and URLs that must survive cleanup.</summary>
public sealed record CorpusItem(string Id, string Input, IReadOnlyList<string> MustKeep);

/// <summary>Parses the fixed cleanup corpus (doc 31, Testing: hardware lane).</summary>
public static class CleanupCorpus
{
    /// <summary>Parses a JSON array of corpus items. Throws <see cref="InvalidDataException"/> on a missing or duplicate id, an empty input or a missing mustKeep list.</summary>
    public static IReadOnlyList<CorpusItem> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var items = JsonSerializer.Deserialize(json, BenchmarkJsonContext.Default.ListCorpusItem)
            ?? throw new InvalidDataException("The cleanup corpus is empty.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Input))
            {
                throw new InvalidDataException("A corpus item has an empty id or input.");
            }

            if (item.MustKeep is null)
            {
                throw new InvalidDataException($"Corpus item '{item.Id}' has no mustKeep list.");
            }

            if (!ids.Add(item.Id))
            {
                throw new InvalidDataException($"Corpus item id '{item.Id}' is duplicated.");
            }
        }

        return items;
    }
}
