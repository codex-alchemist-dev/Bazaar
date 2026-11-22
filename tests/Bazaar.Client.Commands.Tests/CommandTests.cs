using Bazaar.Client.Commands;
using Bazaar.Rubedo.Rules;

namespace Bazaar.Client.Commands.Tests;

public class CommandTests
{
    [Theory]
    [InlineData("w c")]
    [InlineData("gc")]
    [InlineData("v2")]
    [InlineData("A")]
    public void Accepts_letters_digits_and_inner_spaces(string mnemonic)
    {
        var command = new Command("id", "Title", mnemonic, null, CommandSource.Local);

        Assert.Equal(mnemonic, command.Mnemonic);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" gc")]
    [InlineData("gc ")]
    [InlineData("ctrl+s")]
    [InlineData("g-c")]
    [InlineData("<esc>")]
    public void Rejects_empty_or_non_alphanumeric_mnemonics(string mnemonic)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new Command("id", "Title", mnemonic, null, CommandSource.Local));
    }

    [Fact]
    public void Enabled_is_evaluated_each_time_it_is_asked()
    {
        var enabled = true;
        var command = new Command("id", "Title", "t", null, CommandSource.Local, () => enabled);

        Assert.True(command.IsEnabled());
        enabled = false;
        Assert.False(command.IsEnabled());
    }

    [Fact]
    public void Missing_enabled_predicate_means_enabled()
    {
        var command = new Command("id", "Title", "t", null, CommandSource.Local);

        Assert.True(command.IsEnabled());
    }

    [Fact]
    public void Null_keywords_become_an_empty_list()
    {
        var command = new Command("id", "Title", "t", null, CommandSource.Local);

        Assert.Empty(command.Keywords);
    }

    private static RubedoContext Context(string deviceClass = "phone", bool isOwner = false) =>
        RubedoContext.ForDevice(deviceClass, "single", "folded", 400, 800, 1m, false, isOwner, "light");

    private static Command WithRule(string? rule, Func<bool>? predicate = null) =>
        new("id", "Title", "t", null, CommandSource.Local, predicate, rule);

    [Fact]
    public void EnabledWhen_rule_decides_with_a_context()
    {
        var command = WithRule("deviceClass == \"phone\"");

        Assert.Equal("deviceClass == \"phone\"", command.EnabledWhen);
        Assert.True(command.IsEnabled(Context("phone")));
        Assert.False(command.IsEnabled(Context("desktop")));
    }

    [Theory]
    [InlineData("widthDp >")]
    [InlineData("nope == 1")]
    [InlineData("widthDp + 1")]
    public void Invalid_EnabledWhen_throws_with_the_diagnostic_code(string source)
    {
        var (_, diagnostics) = VisibilityRule.Compile(source);

        var thrown = Assert.Throws<ArgumentException>(() => WithRule(source));

        Assert.Contains($"enabledWhen rejected ({diagnostics[0].Code})", thrown.Message);
        Assert.Equal("enabledWhen", thrown.ParamName);
    }

    [Fact]
    public void Predicate_and_rule_must_both_pass()
    {
        var allow = true;
        var command = WithRule("isOwner", () => allow);

        Assert.True(command.IsEnabled(Context(isOwner: true)));
        Assert.False(command.IsEnabled(Context(isOwner: false)));
        allow = false;
        Assert.False(command.IsEnabled(Context(isOwner: true)));
    }

    [Fact]
    public void Predicate_alone_still_works_with_a_context()
    {
        var allow = false;
        var command = WithRule(null, () => allow);

        Assert.False(command.IsEnabled(Context()));
        allow = true;
        Assert.True(command.IsEnabled(Context()));
    }

    [Fact]
    public void No_rule_and_no_predicate_is_enabled_with_a_context()
    {
        Assert.True(WithRule(null).IsEnabled(Context()));
    }

    [Fact]
    public void Runtime_rule_error_disables_the_command()
    {
        const string source = "1 / (widthDp - 400) > 0";
        var context = Context();

        // Prove the rule itself errors with the division-by-zero code, then that the command fails closed on it.
        (VisibilityRule? rule, _) = VisibilityRule.Compile(source);
        VisibilityOutcome outcome = rule!.Evaluate(context);
        Assert.False(outcome.Visible);
        Assert.Equal("division-by-zero", outcome.Issue?.Code);
        Assert.False(WithRule(source).IsEnabled(context));
    }

    [Fact]
    public void Contextless_IsEnabled_fails_closed_when_a_rule_is_set()
    {
        Assert.False(WithRule("true").IsEnabled());
    }
}
