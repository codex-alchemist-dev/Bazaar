using Bazaar.Client.Input;

namespace Bazaar.Ex.Tests;

public class MnemonicOptionsTests
{
    private static readonly IReadOnlyList<MnemonicAssignment> _effective =
        MnemonicResolver.Resolve(NavigationDefaults.Assignments).Effective;

    private static MnemonicSession NewSession(MnemonicOptions? options = null) =>
        MnemonicSession.TryCreate(MnemonicResolver.Resolve(NavigationDefaults.Assignments), [KeyStroke.OfChar('p')], options)!;

    private static void Type(MnemonicSession session, string text)
    {
        foreach (var c in text)
        {
            session.Handle(KeyStroke.OfChar(c), new RouterContext());
        }
    }

    [Fact]
    public void A_leading_count_is_split_off_and_shown_in_the_explanation()
    {
        var intent = MnemonicGrammar.Parse("3ngc1", _effective);

        Assert.Equal(3, intent.Count);
        Assert.Equal(MnemonicVerb.Navigate, intent.Verb);
        Assert.NotNull(intent.Binding);
        Assert.Contains("(x3)", intent.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_count_the_count_is_one()
    {
        Assert.Equal(1, MnemonicGrammar.Parse("ngc1", _effective).Count);
    }

    [Fact]
    public void A_count_is_clamped_to_the_maximum()
    {
        var options = new MnemonicOptions { MaxCount = 5 };

        Assert.Equal(5, MnemonicGrammar.Parse("99ngc1", _effective, options).Count);
    }

    [Fact]
    public void A_count_with_nothing_after_it_is_not_a_count()
    {
        var intent = MnemonicGrammar.Parse("12", _effective);

        Assert.Equal(1, intent.Count);
        Assert.Null(intent.Binding);
    }

    [Fact]
    public void A_leading_zero_is_not_a_count()
    {
        Assert.Equal(1, MnemonicGrammar.Parse("03ngc1", _effective).Count);
    }

    [Fact]
    public void Counts_can_be_turned_off()
    {
        var intent = MnemonicGrammar.Parse("3ngc1", _effective, new MnemonicOptions { CountsEnabled = false });

        Assert.Equal(1, intent.Count);
        Assert.Null(intent.Binding);
    }

    [Fact]
    public void A_mnemonic_that_starts_with_digits_wins_over_a_count()
    {
        var digitFirst = new MnemonicAssignment("cmd.digits", "Digits", "3x", MnemonicTier.BuiltIn, "test");

        var intent = MnemonicGrammar.Parse("3x", [digitFirst]);

        Assert.Equal(1, intent.Count);
        Assert.Equal("cmd.digits", intent.Binding!.CommandId);
    }

    [Fact]
    public void The_m_verb_navigates_like_n_by_default()
    {
        var intent = MnemonicGrammar.Parse("mgc1", _effective);

        Assert.Equal(MnemonicVerb.Navigate, intent.Verb);
        Assert.Equal("gc1", intent.Code);
    }

    [Fact]
    public void The_verb_letters_can_be_changed()
    {
        var options = new MnemonicOptions { Verbs = new Dictionary<char, MnemonicVerb> { ['g'] = MnemonicVerb.Navigate } };

        Assert.Equal(MnemonicVerb.Run, MnemonicGrammar.Parse("ngc1", _effective, options).Verb);
        Assert.Equal(MnemonicVerb.Navigate, MnemonicGrammar.Parse("ggc1", _effective, options).Verb);
    }

    [Fact]
    public void Shift_Enter_with_text_typed_asks_for_suggestions_and_runs_nothing()
    {
        var session = NewSession();
        Type(session, "ngc");

        var step = session.Handle(KeyStroke.OfKey(NamedKey.Enter, KeyModifiers.Shift), new RouterContext());

        Assert.Equal("ngc", step.AiSuggestFor);
        Assert.Null(step.CommandId);
        Assert.Equal("ngc", session.Typed);
    }

    [Fact]
    public void Shift_Enter_with_nothing_typed_is_not_a_suggestion_request()
    {
        var session = NewSession();

        var step = session.Handle(KeyStroke.OfKey(NamedKey.Enter, KeyModifiers.Shift), new RouterContext());

        Assert.Null(step.AiSuggestFor);
    }

    [Fact]
    public void Plain_Enter_still_runs_the_typed_mnemonic()
    {
        var session = NewSession();
        Type(session, "ngc1");

        var step = session.Handle(KeyStroke.OfKey(NamedKey.Enter), new RouterContext());

        Assert.Null(step.AiSuggestFor);
        Assert.Equal(RouteOutcome.Executed, step.Outcome);
    }

    [Fact]
    public void The_suggest_key_can_be_turned_off()
    {
        var session = NewSession(new MnemonicOptions { AiSuggestEnabled = false });
        Type(session, "ngc");

        var step = session.Handle(KeyStroke.OfKey(NamedKey.Enter, KeyModifiers.Shift), new RouterContext());

        Assert.Null(step.AiSuggestFor);
    }
}
