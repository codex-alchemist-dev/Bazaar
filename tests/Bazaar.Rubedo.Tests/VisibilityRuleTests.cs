using Bazaar.Rubedo.Rules;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Tests;

public class VisibilityRuleTests
{
    private static RubedoContext Phone(decimal widthDp = 400) =>
        RubedoContext.ForDevice("phone", "single", "folded", widthDp, 800, 1m, false, true, "dark");

    private static VisibilityRule Compile(string source)
    {
        (VisibilityRule? rule, IReadOnlyList<RubedoDiagnostic> diagnostics) = VisibilityRule.Compile(source);
        Assert.Empty(diagnostics);
        return rule!;
    }

    [Fact]
    public void Context_values_match_the_standard_schema_types()
    {
        IReadOnlyDictionary<string, object> values = Phone().ToValues();

        Assert.Equal(RubedoSchema.Standard.Identifiers.Count, values.Count);
        foreach (KeyValuePair<string, RubedoType> declared in RubedoSchema.Standard.Identifiers)
        {
            object value = values[declared.Key];
            Assert.True(declared.Value switch
            {
                RubedoType.Bool => value is bool,
                RubedoType.Number => value is decimal,
                _ => value is string,
            });
        }
    }

    [Fact]
    public void Rule_shows_when_true_and_hides_when_false()
    {
        VisibilityRule rule = Compile("deviceClass == \"phone\" && widthDp < 600");

        Assert.True(rule.Evaluate(Phone(400)).Visible);
        Assert.False(rule.Evaluate(Phone(700)).Visible);
        Assert.Null(rule.Evaluate(Phone(700)).Issue);
    }

    [Fact]
    public void Non_bool_rule_is_rejected_at_compile_time()
    {
        (VisibilityRule? rule, IReadOnlyList<RubedoDiagnostic> diagnostics) = VisibilityRule.Compile("widthDp + 1");

        Assert.Null(rule);
        Assert.Equal("result-type", diagnostics[0].Code);
    }

    [Fact]
    public void Unknown_identifier_is_rejected_at_compile_time()
    {
        (VisibilityRule? rule, IReadOnlyList<RubedoDiagnostic> diagnostics) = VisibilityRule.Compile("nope > 1");

        Assert.Null(rule);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void Runtime_error_hides_and_reports_an_issue()
    {
        VisibilityRule rule = Compile("10 / (widthDp - 400) > 1");

        VisibilityOutcome outcome = rule.Evaluate(Phone(400));

        Assert.False(outcome.Visible);
        Assert.NotNull(outcome.Issue);
    }
}
