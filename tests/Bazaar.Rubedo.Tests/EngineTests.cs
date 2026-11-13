namespace Bazaar.Rubedo.Tests;

public class EngineTests
{
    private static Dictionary<string, object> Ctx(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void Compile_reports_the_inferred_result_type()
    {
        RubedoCompileResult result = RubedoEngine.Compile("widthDp > 600");

        Assert.True(result.IsValid);
        Assert.Equal(RubedoType.Bool, result.Program!.ResultType);
    }

    [Fact]
    public void Required_type_is_enforced_at_compile_time()
    {
        RubedoCompileResult result = RubedoEngine.Compile("widthDp + 1", requiredType: RubedoType.Bool);

        Assert.False(result.IsValid);
        Assert.Equal("result-type", result.Diagnostics[0].Code);
    }

    [Fact]
    public void Diagnostics_carry_the_source_offset()
    {
        RubedoCompileResult result = RubedoEngine.Compile("widthDp > nope");

        Assert.Equal("unknown-identifier", result.Diagnostics[0].Code);
        Assert.Equal(10, result.Diagnostics[0].Offset);
    }

    [Fact]
    public void Options_can_tighten_limits_but_never_loosen_them()
    {
        var tight = new RubedoOptions(MaxSourceChars: 5);
        Assert.Equal("too-long", RubedoEngine.Compile("true && true", options: tight).Diagnostics[0].Code);

        var loose = new RubedoOptions(MaxSourceChars: 100_000, MaxSteps: 100_000).Clamped();
        Assert.Equal(512, loose.MaxSourceChars);
        Assert.Equal(1000, loose.MaxSteps);
    }

    [Fact]
    public void Step_budget_stops_an_expression_that_is_too_big_to_run()
    {
        var options = new RubedoOptions(MaxSteps: 5);
        RubedoProgram program = RubedoEngine.Compile("1 + 2 + 3 + 4 + 5 == 15", options: options).Program!;

        RubedoEvaluation result = RubedoEngine.Evaluate(program, Ctx());

        Assert.Equal("budget", result.Error!.Code);
    }

    [Fact]
    public void Deep_nesting_is_rejected()
    {
        string source = new string('(', 40) + "1" + new string(')', 40) + " == 1";

        Assert.Equal("too-deep", RubedoEngine.Compile(source).Diagnostics[0].Code);
    }

    [Fact]
    public void Visibility_fails_closed_with_a_notice()
    {
        RubedoProgram program = RubedoEngine.Compile("1 / widthDp > 0").Program!;

        RubedoVisibility visibility = RubedoEngine.IsVisible(program, Ctx(("widthDp", 0m)));

        Assert.False(visibility.Visible);
        Assert.Equal("division-by-zero", visibility.Notice!.Code);
    }

    [Fact]
    public void Visibility_shows_when_the_rule_is_true()
    {
        RubedoProgram program = RubedoEngine.Compile("widthDp >= 600").Program!;

        RubedoVisibility visibility = RubedoEngine.IsVisible(program, Ctx(("widthDp", 840m)));

        Assert.True(visibility.Visible);
        Assert.Null(visibility.Notice);
    }

    [Fact]
    public void Visibility_rejects_a_non_boolean_rule()
    {
        RubedoProgram program = RubedoEngine.Compile("widthDp").Program!;

        RubedoVisibility visibility = RubedoEngine.IsVisible(program, Ctx(("widthDp", 840m)));

        Assert.False(visibility.Visible);
        Assert.Equal("result-type", visibility.Notice!.Code);
    }

    [Fact]
    public void Evaluation_is_deterministic_for_the_same_context()
    {
        RubedoProgram program = RubedoEngine.Compile("0.1 * 3 == 0.3").Program!;

        Assert.Equal(true, RubedoEngine.Evaluate(program, Ctx()).Value);
        Assert.Equal(true, RubedoEngine.Evaluate(program, Ctx()).Value);
    }

    [Fact]
    public void Language_name_is_one_constant()
    {
        Assert.Equal("Rubedo", RubedoLanguage.Name);
        Assert.Equal(1, RubedoLanguage.Version);
    }
}
