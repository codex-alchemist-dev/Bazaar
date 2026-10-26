using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Tests.Sanitization;

/// <summary>Runs the golden leak corpus (Golden/leak-corpus.json) through the sanitizer.</summary>
public sealed class GoldenCorpusTests
{
    private const string _corpusPath = "Sanitization/Golden/leak-corpus.json";
    private const int _minimumCases = 40;

    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void AllGoldenCasesMatchExpectedOutput()
    {
        var failures = new List<string>();
        foreach (CorpusCase testCase in LoadCases())
        {
            string actual = Sanitize(testCase);
            if (!string.Equals(actual, testCase.Expected, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected [{testCase.Expected}] but was [{actual}]");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void NoPlantedSecretOrNameLeaks()
    {
        var failures = new List<string>();
        foreach (CorpusCase testCase in LoadCases())
        {
            List<string> planted = testCase.Planted ?? [];
            bool exemptFromPlanted = testCase.Name.StartsWith("clean-", StringComparison.Ordinal)
                || testCase.Name.StartsWith("empty-", StringComparison.Ordinal);
            if (planted.Count == 0 && !exemptFromPlanted)
            {
                failures.Add($"{testCase.Name}: no planted values declared");
                continue;
            }

            string actual = Sanitize(testCase);
            foreach (string value in planted)
            {
                if (actual.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{testCase.Name}: leaked [{value}] in output [{actual}]");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void CorpusIsNotEmpty()
    {
        IReadOnlyList<CorpusCase> cases = LoadCases();

        Assert.True(cases.Count >= _minimumCases, $"expected at least {_minimumCases} cases but found {cases.Count}");
    }

    [Fact]
    public void EveryPlantedValueAppearsInInput()
    {
        var failures = new List<string>();
        foreach (CorpusCase testCase in LoadCases())
        {
            foreach (string value in testCase.Planted ?? [])
            {
                if (!testCase.Input.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{testCase.Name}: planted [{value}] is not in the input");
                }
            }
        }

        Assert.Empty(failures);
    }

    private static List<CorpusCase> LoadCases()
    {
        string path = Path.Combine(AppContext.BaseDirectory, _corpusPath);
        CorpusFile file = JsonSerializer.Deserialize<CorpusFile>(Expand(File.ReadAllText(path)), _options)
            ?? throw new InvalidOperationException($"corpus file is empty: {path}");
        return file.Cases ?? throw new InvalidOperationException($"corpus file has no cases: {path}");
    }

    // Secret-shaped values are assembled here, not stored in the corpus, so no repo scanner sees a
    // token-shaped literal. The corpus refers to them as {{name}}; the fragments keep the sanitizer's
    // patterns intact because they are joined before the sanitizer sees them.
    private static readonly Dictionary<string, string> _fragments = new()
    {
        ["anthropic"] = "sk-" + "ant-api03-" + "Qx7Lm2Vb9Zt4Rw8Ys",
        ["openai"] = "sk-" + "proj1234567890abcdefGH",
        ["github"] = "gh" + "p_" + "aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW3xY5z",
        ["githubPat"] = "github" + "_pat_" + "11ABCDEFGH0123456789_xyz",
        ["aws"] = "AK" + "IAIOSFODNN7EXAMPLE",
        ["slack"] = "xox" + "b-1234567890-abcdefghij",
        ["google"] = "AIz" + "aSyA1B2C3D4E5F6G7H8I9J0K1L2M3N4O5P6Q",
        ["jwt"] = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbmEifQ.dGhpc2lzYXNpZw",
        ["pemOpenOpenssh"] = "-----BEGIN " + "OPENSSH PRIVATE KEY-----",
        ["pemCloseOpenssh"] = "-----END " + "OPENSSH PRIVATE KEY-----",
        ["pemOpenRsa"] = "-----BEGIN " + "RSA PRIVATE KEY-----",
        ["pemCloseRsa"] = "-----END " + "RSA PRIVATE KEY-----",
        ["bearerA"] = "abcdef1234567890XY",
        ["bearerB"] = "9fLkQ2mZx7Vb4Nt",
        ["pwA"] = "hunter" + "22",
        ["pwB"] = "hunter" + "2",
        ["tokenA"] = "qwerty9988",
        ["apiKeyA"] = "k9m2x7q4",
        ["accessKeyA"] = "AK9xyz3",
        ["pwC"] = "Xy9pass",
        ["apiKeyB"] = "Jq8Zr3Lm1",
        ["pwD"] = "Pw8kTn3",
    };

    private static string Expand(string json) =>
        Regex.Replace(json, @"\{\{(\w+)\}\}", match => _fragments[match.Groups[1].Value]);

    private static string Sanitize(CorpusCase testCase)
    {
        CorpusTerms terms = testCase.Terms ?? new CorpusTerms(null, null, null, null, null);
        var sensitive = new SensitiveTerms(
            Values(terms.People),
            Values(terms.Agents),
            Values(terms.Repositories),
            Values(terms.Customers),
            Values(terms.ExternalQuotes));
        return new TextSanitizer().Sanitize(testCase.Input, sensitive).Text;
    }

    private static string[] Values(List<string>? values) => values?.ToArray() ?? [];

    private sealed record CorpusFile(List<CorpusCase>? Cases);

    private sealed record CorpusCase(
        string Name,
        string Input,
        CorpusTerms? Terms,
        List<string>? Planted,
        string Expected);

    private sealed record CorpusTerms(
        List<string>? People,
        List<string>? Agents,
        List<string>? Repositories,
        List<string>? Customers,
        List<string>? ExternalQuotes);
}
