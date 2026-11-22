using System.Text.Json;

namespace Bazaar.Rubedo.Analysis.Tests;

/// <summary>
/// Robustness loops (doc 41d section 4): every entry point must return, never throw, and keep its ranges inside the text, for
/// every truncation and for seeded random mutations of every golden-corpus source.
/// </summary>
public class FuzzTests
{
    private static string[] ExpressionSources()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "rubedo-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("source").GetString()!).ToArray();
    }

    private static List<string> ViewSources()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Golden");
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "rubedo-views-v1.json")));
        var sources = corpus.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("document").GetRawText()).ToList();
        sources.Add(File.ReadAllText(Path.Combine(root, "pilots", "gc-feed.view.json")));
        return sources;
    }

    private static void ExerciseExpression(string text, int offset)
    {
        foreach (AnalysisDiagnostic d in RubedoAnalyzer.Diagnose(text))
        {
            AssertRange(d.Start, d.End, text.Length);
        }

        CompletionList list = RubedoAnalyzer.Complete(text, offset);
        AssertRange(list.ReplaceStart, list.ReplaceEnd, text.Length);
        if (RubedoAnalyzer.Hover(text, offset) is { } hover)
        {
            AssertRange(hover.Start, hover.End, text.Length);
        }

        if (RubedoAnalyzer.SignatureHelp(text, offset) is { } help)
        {
            Assert.InRange(help.ActiveParameter, 0, help.Parameters.Count);
        }
    }

    private static void ExerciseView(string text, int offset)
    {
        foreach (AnalysisDiagnostic d in RubedoAnalyzer.DiagnoseView(text))
        {
            AssertRange(d.Start, d.End, text.Length);
        }

        CompletionList list = RubedoAnalyzer.CompleteView(text, offset);
        AssertRange(list.ReplaceStart, list.ReplaceEnd, text.Length);
    }

    private static void AssertRange(int start, int end, int length)
    {
        Assert.InRange(start, 0, length);
        Assert.InRange(end, start, length);
    }

    [Fact]
    public void Every_truncation_of_every_golden_expression_is_safe()
    {
        foreach (string source in ExpressionSources())
        {
            for (int length = 0; length <= source.Length; length++)
            {
                string truncated = source[..length];
                ExerciseExpression(truncated, length);
                ExerciseExpression(truncated, length / 2);
            }
        }
    }

    [Fact]
    public void Every_suffix_and_every_cursor_of_every_golden_expression_is_safe()
    {
        foreach (string source in ExpressionSources())
        {
            for (int offset = -2; offset <= source.Length + 2; offset++)
            {
                ExerciseExpression(source, offset);
            }

            for (int start = 0; start < source.Length; start++)
            {
                ExerciseExpression(source[start..], 0);
            }
        }
    }

    [Fact]
    public void Random_mutations_of_golden_expressions_are_safe()
    {
        var random = new Random(41);
        const string alphabet = "()\",!<>=&|+-*/%.0123456789 abcdefxyz\\\né😀";
        foreach (string source in ExpressionSources())
        {
            for (int round = 0; round < 25; round++)
            {
                var chars = source.ToCharArray().ToList();
                int edits = random.Next(1, 4);
                for (int e = 0; e < edits; e++)
                {
                    int at = chars.Count == 0 ? 0 : random.Next(chars.Count + 1);
                    switch (random.Next(3))
                    {
                        case 0:
                            chars.Insert(at, alphabet[random.Next(alphabet.Length)]);
                            break;
                        case 1 when at < chars.Count:
                            chars.RemoveAt(at);
                            break;
                        default:
                            if (at < chars.Count)
                            {
                                chars[at] = alphabet[random.Next(alphabet.Length)];
                            }

                            break;
                    }
                }

                string mutated = new(chars.ToArray());
                ExerciseExpression(mutated, random.Next(mutated.Length + 1));
            }
        }
    }

    [Fact]
    public void Every_truncation_of_every_golden_view_document_is_safe()
    {
        foreach (string source in ViewSources())
        {
            int step = Math.Max(1, source.Length / 400);
            for (int length = 0; length <= source.Length; length += step)
            {
                string truncated = source[..length];
                ExerciseView(truncated, length);
                ExerciseView(truncated, length / 2);
            }

            ExerciseView(source, source.Length);
        }
    }

    [Fact]
    public void Every_cursor_in_every_golden_view_document_is_safe()
    {
        foreach (string source in ViewSources().Take(12))
        {
            for (int offset = 0; offset <= source.Length; offset++)
            {
                AssertRange(RubedoAnalyzer.CompleteView(source, offset).ReplaceStart, RubedoAnalyzer.CompleteView(source, offset).ReplaceEnd, source.Length);
            }
        }
    }

    [Fact]
    public void Random_mutations_of_golden_view_documents_are_safe()
    {
        var random = new Random(4141);
        const string alphabet = "{}[]\":,\\ \n0123456789truefalsenullé€";
        foreach (string source in ViewSources())
        {
            for (int round = 0; round < 10; round++)
            {
                var chars = source.ToCharArray().ToList();
                for (int e = 0; e < 3; e++)
                {
                    int at = random.Next(chars.Count + 1);
                    if (random.Next(2) == 0 || at >= chars.Count)
                    {
                        chars.Insert(at, alphabet[random.Next(alphabet.Length)]);
                    }
                    else
                    {
                        chars.RemoveAt(at);
                    }
                }

                string mutated = new(chars.ToArray());
                ExerciseView(mutated, random.Next(mutated.Length + 1));
            }
        }
    }

    [Fact]
    public void Pathological_inputs_are_safe()
    {
        string[] inputs =
        [
            new string('(', 5000),
            new string(')', 5000),
            new string('"', 5000),
            string.Concat(Enumerable.Repeat("min(", 3000)),
            string.Concat(Enumerable.Repeat("1,", 20000)),
            new string('-', 60000),
            new string('{', 60000),
            new string('[', 60000),
            "\0\u0001￿\uD800",
        ];
        foreach (string input in inputs)
        {
            ExerciseExpression(input, input.Length / 2);
            ExerciseExpression(input, input.Length);
            ExerciseView(input, input.Length / 2);
            ExerciseView(input, input.Length);
        }
    }

    [Fact]
    public void Results_are_deterministic()
    {
        foreach (string source in ExpressionSources())
        {
            Assert.Equal(RubedoAnalyzer.Diagnose(source), RubedoAnalyzer.Diagnose(source));
            Assert.Equal(RubedoAnalyzer.Complete(source, source.Length / 2).Items, RubedoAnalyzer.Complete(source, source.Length / 2).Items);
            Assert.Equal(RubedoAnalyzer.Hover(source, 1), RubedoAnalyzer.Hover(source, 1));
        }
    }

    [Fact]
    public void Golden_expressions_diagnose_exactly_as_the_engine_compiles()
    {
        foreach (string source in ExpressionSources())
        {
            bool valid = RubedoEngine.Compile(source).IsValid;
            Assert.Equal(valid, RubedoAnalyzer.Diagnose(source).Count == 0);
        }
    }

    [Fact]
    public void Golden_view_documents_diagnose_exactly_as_the_validator_does()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "rubedo-views-v1.json");
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonElement c in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            string json = c.GetProperty("document").GetRawText();
            JsonElement expect = c.GetProperty("expect");
            IReadOnlyList<AnalysisDiagnostic> actual = RubedoAnalyzer.DiagnoseView(json);
            if (expect.TryGetProperty("readError", out JsonElement readError) && readError.GetBoolean())
            {
                Assert.Single(actual);
                Assert.StartsWith("view.", actual[0].Code);
                continue;
            }

            string[] expected = expect.GetProperty("codes").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal(expected, actual.Select(d => d.Code).ToArray());
        }
    }
}
