namespace Bazaar.Rubedo.Tests;

/// <summary>
/// Unit tests for the diagnostics and limits that the golden corpus cannot express: options that tighten limits,
/// a context that supplies null, required result types, and the offsets that point at the problem (doc 41a sections 6 and 7).
/// </summary>
public class DiagnosticCoverageTests
{
    private static Dictionary<string, object> Ctx(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>A chain of <paramref name="count"/> tokens: a leading "-1" then "+1" pairs (each pair is two tokens).</summary>
    private static string TokenChain(int count)
    {
        // "-1" is two tokens and each "+1" is two more, so the count must be even.
        Assert.Equal(0, count % 2);
        var source = new System.Text.StringBuilder("-1");
        for (int pair = 0; pair < (count - 2) / 2; pair++)
        {
            source.Append("+1");
        }

        return source.ToString();
    }

    [Fact]
    public void Expression_of_257_tokens_is_rejected_with_its_offset()
    {
        string source = TokenChain(258);

        RubedoCompileResult result = RubedoEngine.Compile(source);

        Assert.Equal("too-many-tokens", result.Diagnostics[0].Code);
        Assert.Equal(256, result.Diagnostics[0].Offset);
    }

    [Fact]
    public void Options_cannot_loosen_the_token_limit()
    {
        string source = TokenChain(258);

        RubedoCompileResult result = RubedoEngine.Compile(source, options: new RubedoOptions(MaxTokens: 10_000));

        Assert.Equal("too-many-tokens", result.Diagnostics[0].Code);
    }

    /// <summary>
    /// BUG (found by the coverage pass, not fixed here): an expression of exactly 256 tokens is rejected. The lexer's end-of-input
    /// check uses <c>&gt;=</c> against the limit, so a valid 256-token source fails. Doc 41a section 6 sets 256 as the limit, so
    /// it should compile. This test is red on purpose until the owner or the engine owner decides the fix.
    /// </summary>
    [Fact]
    public void KnownBug_expression_of_exactly_256_tokens_is_wrongly_rejected()
    {
        string source = TokenChain(256);

        RubedoCompileResult result = RubedoEngine.Compile(source);

        string code = result.Diagnostics.Count > 0 ? result.Diagnostics[0].Code : "none";
        Assert.True(result.IsValid, $"A 256-token expression should compile; got '{code}'.");
        Assert.Equal(126m, RubedoEngine.Evaluate(result.Program!, Ctx()).Value);
    }

    [Fact]
    public void Step_budget_allows_exactly_as_many_steps_as_nodes()
    {
        // "1 + 2" visits three nodes (the sum and its two literals), so three steps are enough and two are not.
        RubedoProgram enough = RubedoEngine.Compile("1 + 2 == 3", options: new RubedoOptions(MaxSteps: 5)).Program!;
        RubedoProgram tooFew = RubedoEngine.Compile("1 + 2", options: new RubedoOptions(MaxSteps: 2)).Program!;
        RubedoProgram exact = RubedoEngine.Compile("1 + 2", options: new RubedoOptions(MaxSteps: 3)).Program!;

        Assert.Equal("budget", RubedoEngine.Evaluate(tooFew, Ctx()).Error!.Code);
        Assert.Equal(3m, RubedoEngine.Evaluate(exact, Ctx()).Value);
        Assert.Equal(true, RubedoEngine.Evaluate(enough, Ctx()).Value);
    }

    [Theory]
    [InlineData("\"a\rb\"")]
    [InlineData("\"a\nb\"")]
    public void Line_breaks_inside_a_string_are_rejected(string source)
    {
        Assert.Equal("bad-string", RubedoEngine.Compile(source).Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("99999999999999999999999999999")]
    [InlineData("1000000000000000000000000000000")]
    public void Literals_too_big_for_the_decimal_type_are_out_of_range(string source)
    {
        Assert.Equal("number-out-of-range", RubedoEngine.Compile(source).Diagnostics[0].Code);
    }

    [Fact]
    public void Options_can_tighten_the_string_limit_but_not_loosen_it()
    {
        string longString = "\"" + new string('a', 65) + "\" == \"\"";

        Assert.Equal("string-too-long", RubedoEngine.Compile(longString, options: new RubedoOptions(MaxStringChars: 100)).Diagnostics[0].Code);
        Assert.Equal("string-too-long", RubedoEngine.Compile(longString, options: new RubedoOptions(MaxStringChars: 64)).Diagnostics[0].Code);
    }

    [Fact]
    public void Options_can_tighten_the_depth_limit()
    {
        Assert.True(RubedoEngine.Compile("((1)) == 1", options: new RubedoOptions(MaxDepth: 2)).IsValid);
        Assert.Equal("too-deep", RubedoEngine.Compile("(((1))) == 1", options: new RubedoOptions(MaxDepth: 2)).Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("1 @ 2", 2)]
    [InlineData("1 = 1", 2)]
    [InlineData("2x == 2", 1)]
    [InlineData("1 +", 3)]
    [InlineData("abs(1", 5)]
    [InlineData("1 == 1 == true", 7)]
    public void Diagnostics_point_at_the_token_that_is_wrong(string source, int offset)
    {
        RubedoCompileResult result = RubedoEngine.Compile(source);

        Assert.False(result.IsValid);
        Assert.Equal(offset, result.Diagnostics[0].Offset);
    }

    [Fact]
    public void Required_result_type_is_reported_at_the_root_operator()
    {
        RubedoCompileResult result = RubedoEngine.Compile("1 + 1", requiredType: RubedoType.Bool);

        Assert.Equal("result-type", result.Diagnostics[0].Code);
        Assert.Equal(2, result.Diagnostics[0].Offset);
    }

    [Fact]
    public void A_context_value_of_the_wrong_type_is_a_context_error()
    {
        RubedoProgram program = RubedoEngine.Compile("isOwner").Program!;

        RubedoEvaluation result = RubedoEngine.Evaluate(program, Ctx(("isOwner", 1m)));

        Assert.Equal("context-type", result.Error!.Code);
    }

    [Fact]
    public void A_null_context_value_counts_as_missing()
    {
        RubedoProgram program = RubedoEngine.Compile("widthDp > 0").Program!;

        RubedoEvaluation result = RubedoEngine.Evaluate(program, Ctx(("widthDp", null!)));

        Assert.Equal("missing-value", result.Error!.Code);
    }

    [Fact]
    public void Visibility_with_a_missing_value_hides_the_pane_with_a_notice()
    {
        RubedoProgram program = RubedoEngine.Compile("widthDp >= 600").Program!;

        RubedoVisibility visibility = RubedoEngine.IsVisible(program, Ctx());

        Assert.False(visibility.Visible);
        Assert.Equal("missing-value", visibility.Notice!.Code);
    }

    [Fact]
    public void Visibility_with_a_bad_range_clamp_hides_the_pane_with_a_notice()
    {
        RubedoProgram program = RubedoEngine.Compile("clamp(widthDp, 10, 0) == 5").Program!;

        RubedoVisibility visibility = RubedoEngine.IsVisible(program, Ctx(("widthDp", 5m)));

        Assert.False(visibility.Visible);
        Assert.Equal("bad-range", visibility.Notice!.Code);
    }

    [Fact]
    public void Context_keys_outside_the_schema_do_not_affect_evaluation()
    {
        RubedoProgram program = RubedoEngine.Compile("widthDp > 0").Program!;

        RubedoEvaluation result = RubedoEngine.Evaluate(program, Ctx(("widthDp", 10m), ("notInSchema", "x")));

        Assert.Equal(true, result.Value);
    }
}
