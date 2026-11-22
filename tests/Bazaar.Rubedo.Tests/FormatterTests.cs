using System.Text.Json;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Tests;

public class FormatterTests
{
    [Theory]
    [InlineData("1+2*3", "1 + 2 * 3")]
    [InlineData("( 1 + 2 ) * 3", "(1 + 2) * 3")]
    [InlineData("((1))", "1")]
    [InlineData("(1 * 2) + 3", "1 * 2 + 3")]
    [InlineData("1 - (2 - 3)", "1 - (2 - 3)")]
    [InlineData("(1 - 2) - 3", "1 - 2 - 3")]
    [InlineData("min( 1 ,2 )", "min(1, 2)")]
    [InlineData("-(1+2)", "-(1 + 2)")]
    [InlineData("-(-3)", "--3")]
    [InlineData("!(true&&false)", "!(true && false)")]
    [InlineData("(1 < 2) == true", "(1 < 2) == true")]
    [InlineData("true == (1 < 2)", "true == (1 < 2)")]
    [InlineData("(1 == 1) == true", "(1 == 1) == true")]
    [InlineData("true == (1 == 1)", "true == (1 == 1)")]
    [InlineData("(1 == 1) == (2 == 2)", "(1 == 1) == (2 == 2)")]
    [InlineData("(1 + 2 < 4) == (5 >= 6)", "(1 + 2 < 4) == (5 >= 6)")]
    [InlineData("1 + 2 < 4 + 5", "1 + 2 < 4 + 5")]
    [InlineData("(true == false) == (true != false)", "(true == false) == (true != false)")]
    [InlineData("(true || false) && true", "(true || false) && true")]
    [InlineData("true || (false && true)", "true || false && true")]
    [InlineData("1.50 + 007 + 2.0", "1.5 + 7 + 2")]
    [InlineData("0.10 == 0", "0.1 == 0")]
    [InlineData("0.0 == 0", "0 == 0")]
    [InlineData("100 == 100.00", "100 == 100")]
    [InlineData("\"a  b\"==\"a  b\"", "\"a  b\" == \"a  b\"")]
    [InlineData("  true \n && false ", "true && false")]
    public void Formats_to_the_canonical_form(string input, string expected)
    {
        RubedoFormatResult result = RubedoFormatter.Format(input);

        Assert.True(result.IsValid, string.Join(";", result.Diagnostics));
        Assert.Equal(expected, result.Formatted);
        Assert.True(RubedoEngine.Compile(result.Formatted!).IsValid);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("widht > 0")]
    [InlineData("1 < 2 < 3")]
    [InlineData("\"unterminated")]
    [InlineData("")]
    public void Invalid_source_returns_the_original_diagnostics(string source)
    {
        RubedoFormatResult result = RubedoFormatter.Format(source);
        RubedoCompileResult compiled = RubedoEngine.Compile(source);

        Assert.False(result.IsValid);
        Assert.Null(result.Formatted);
        Assert.Equal(compiled.Diagnostics, result.Diagnostics);
    }

    [Fact]
    public void Output_over_the_length_limit_is_a_diagnostic()
    {
        // 128 terms of "10" fit in 383 characters and 255 tokens, but the canonical form adds two spaces per operator.
        string source = string.Join("+", Enumerable.Repeat("10", 128));
        Assert.True(RubedoEngine.Compile(source).IsValid);

        RubedoFormatResult result = RubedoFormatter.Format(source);

        Assert.False(result.IsValid);
        Assert.Equal("too-long", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Formats_against_a_schema()
    {
        Assert.Equal("widthDp >= 600", RubedoFormatter.Format("widthDp>=600", RubedoSchema.Standard).Formatted);
    }

    [Fact]
    public void Golden_corpus_is_idempotent_and_semantics_preserving()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "rubedo-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        var failures = new List<string>();
        int formatted = 0;
        foreach (JsonElement testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = testCase.GetProperty("name").GetString()!;
            string source = testCase.GetProperty("source").GetString()!;
            RubedoCompileResult original = RubedoEngine.Compile(source);
            RubedoFormatResult result = RubedoFormatter.Format(source);

            if (!original.IsValid)
            {
                if (result.IsValid || !result.Diagnostics.SequenceEqual(original.Diagnostics))
                {
                    failures.Add($"{name}: invalid source did not return the original diagnostics");
                }

                continue;
            }

            if (!result.IsValid)
            {
                failures.Add($"{name}: valid source failed to format: {result.Diagnostics[0].Code}");
                continue;
            }

            formatted++;
            string output = result.Formatted!;
            if (output.Length > 512)
            {
                failures.Add($"{name}: output exceeds 512 characters");
            }

            if (RubedoFormatter.Format(output).Formatted != output)
            {
                failures.Add($"{name}: not idempotent: '{output}'");
            }

            RubedoCompileResult again = RubedoEngine.Compile(output);
            if (!again.IsValid)
            {
                failures.Add($"{name}: formatted source does not compile: '{output}'");
                continue;
            }

            Dictionary<string, object> context = ReadContext(testCase.GetProperty("context"));
            RubedoEvaluation before = RubedoEngine.Evaluate(original.Program!, context);
            RubedoEvaluation after = RubedoEngine.Evaluate(again.Program!, context);
            if (!Equals(before.Value, after.Value) || before.Error?.Code != after.Error?.Code || original.Program!.ResultType != again.Program!.ResultType)
            {
                failures.Add($"{name}: result changed for '{output}'");
            }
        }

        Assert.True(formatted >= 30, $"Only {formatted} corpus cases compiled.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
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
