using System.Text.Json;
using Bazaar.Rubedo;

namespace Bazaar.Rubedo.Tests;

/// <summary>
/// Runs the shared golden corpus (doc 41a section 8). Every head runs the same JSON file, so a disagreement fails CI.
/// Each case gives a source, a context and one expectation: <c>value</c>, <c>error</c> (evaluation), <c>compileError</c> or <c>contextError</c>.
/// </summary>
public class GoldenCorpusTests
{
    [Fact]
    public void Every_golden_case_matches_the_engine()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "rubedo-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("rubedo", root.GetProperty("language").GetString());

        var failures = new List<string>();
        int count = 0;
        foreach (JsonElement caseElement in root.GetProperty("cases").EnumerateArray())
        {
            count++;
            string name = caseElement.GetProperty("name").GetString()!;
            string? problem = Check(caseElement);
            if (problem is not null)
            {
                failures.Add($"{name}: {problem}");
            }
        }

        Assert.True(count >= 40, $"The corpus has {count} cases; expected at least 40.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string? Check(JsonElement caseElement)
    {
        string source = caseElement.GetProperty("source").GetString()!;
        JsonElement expect = caseElement.GetProperty("expect");
        RubedoCompileResult compiled = RubedoEngine.Compile(source);

        if (expect.TryGetProperty("compileError", out JsonElement compileCode))
        {
            if (compiled.IsValid)
            {
                return $"expected compile error '{compileCode.GetString()}' but the source compiled";
            }

            string actual = compiled.Diagnostics[0].Code;
            return actual == compileCode.GetString() ? null : $"expected compile error '{compileCode.GetString()}', got '{actual}'";
        }

        if (!compiled.IsValid)
        {
            return $"unexpected compile error '{compiled.Diagnostics[0].Code}': {compiled.Diagnostics[0].Message}";
        }

        Dictionary<string, object> context = ReadContext(caseElement.GetProperty("context"));
        RubedoEvaluation evaluation = RubedoEngine.Evaluate(compiled.Program!, context);

        if (expect.TryGetProperty("contextError", out JsonElement contextCode))
        {
            return evaluation.Error?.Code == contextCode.GetString()
                ? null
                : $"expected context error '{contextCode.GetString()}', got '{evaluation.Error?.Code ?? "none"}'";
        }

        if (expect.TryGetProperty("error", out JsonElement errorCode))
        {
            return evaluation.Error?.Code == errorCode.GetString()
                ? null
                : $"expected error '{errorCode.GetString()}', got '{evaluation.Error?.Code ?? "value " + evaluation.Value}'";
        }

        if (evaluation.Error is not null)
        {
            return $"unexpected error '{evaluation.Error.Code}': {evaluation.Error.Message}";
        }

        JsonElement value = expect.GetProperty("value");
        object expected = value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDecimal(),
            _ => value.GetString()!,
        };
        return Equals(expected, evaluation.Value) ? null : $"expected {expected}, got {evaluation.Value}";
    }

    private static Dictionary<string, object> ReadContext(JsonElement context)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (JsonProperty property in context.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.GetDecimal(),
                _ => property.Value.GetString()!,
            };
        }

        return result;
    }
}
