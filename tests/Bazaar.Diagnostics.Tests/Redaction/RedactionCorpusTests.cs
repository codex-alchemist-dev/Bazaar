using System.Text.Json;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Diagnostics.Tests.Redaction;

public class RedactionCorpusTests
{
    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Redaction", "RedactionCorpus", "redaction-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    // The corpus stores five-dash runs as "~DASH5~" so the file holds no PEM-shaped literals.
    // Fixtures are rebuilt here at test time, so the secret scanner never sees a key-shaped string.
    private static string Expand(string text) => text.Replace("~DASH5~", new string('-', 5), StringComparison.Ordinal);

    [Fact]
    public void Corpus_outputs_match_expected_text()
    {
        var failures = new List<string>();
        var redactor = new Redactor();
        foreach (var item in LoadCorpus().GetProperty("cases").EnumerateArray())
        {
            var input = Expand(item.GetProperty("input").GetString()!);
            var expected = Expand(item.GetProperty("expected").GetString()!);
            var result = redactor.Redact(RedactionClass.FreeText, input);
            if (result.Status != RedactionStatus.Ok || result.Text != expected)
            {
                failures.Add($"[{item.GetProperty("rule").GetString()} {item.GetProperty("kind").GetString()}] input={input:l} expected={expected:l} actual={result.Text:l} status={result.Status}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Corpus_has_five_positive_and_five_negative_cases_for_every_rule()
    {
        var counts = new Dictionary<(string Rule, string Kind), int>();
        foreach (var item in LoadCorpus().GetProperty("cases").EnumerateArray())
        {
            var key = (item.GetProperty("rule").GetString()!, item.GetProperty("kind").GetString()!);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var rule in RedactionRules.V1)
        {
            Assert.True(counts.GetValueOrDefault((rule.Id, "positive")) >= 5, $"{rule.Id} needs 5 positive cases");
            Assert.True(counts.GetValueOrDefault((rule.Id, "negative")) >= 5, $"{rule.Id} needs 5 negative cases");
        }

        var known = RedactionRules.V1.Select(rule => rule.Id).ToHashSet();
        Assert.All(counts.Keys, key => Assert.Contains(key.Rule, known));
    }

    [Fact]
    public void Corpus_is_pinned_to_the_rules_version()
    {
        Assert.Equal(RedactionRules.Version, LoadCorpus().GetProperty("rulesVersion").GetString());
    }
}
