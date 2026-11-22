using Bazaar.Client.Input;
using Bazaar.Ex;

namespace Bazaar.Ex.Tests;

public class MnemonicGrammarTests
{
    private static readonly IReadOnlyList<MnemonicAssignment> _effective =
        MnemonicResolver.Resolve(NavigationDefaults.Assignments).Effective;

    [Fact]
    public void Verb_n_with_a_code_is_a_navigation_and_the_overlay_says_where()
    {
        var intent = MnemonicGrammar.Parse("ngc1", _effective);

        Assert.Equal(MnemonicVerb.Navigate, intent.Verb);
        Assert.Equal("gc1", intent.Code);
        Assert.Equal("Move to: GC list, first chat", intent.Explanation);
    }

    [Fact]
    public void Nrfdrm_moves_to_the_ryusei_roadmap()
    {
        var intent = MnemonicGrammar.Parse("nrfdrm", _effective);

        Assert.Equal(NavigationDefaults.RyuseiRoadmapCommandId, intent.Binding?.CommandId);
        Assert.Equal("Move to: RyuseiFuda Roadmap", intent.Explanation);
    }

    [Fact]
    public void Named_targets_are_run_without_a_verb()
    {
        var intent = MnemonicGrammar.Parse("topgc", _effective);

        Assert.Equal(MnemonicVerb.Run, intent.Verb);
        Assert.Equal(NavigationDefaults.GcTopCommandId, intent.Binding?.CommandId);
        Assert.Equal("Run: Most recent activity GC", intent.Explanation);
    }

    [Theory]
    [InlineData("unread", NavigationDefaults.UnreadCommandId)]
    [InlineData("settings", NavigationDefaults.SettingsCommandId)]
    [InlineData("constellation", NavigationDefaults.ConstellationCommandId)]
    [InlineData("kanban", NavigationDefaults.RyuseiBoardCommandId)]
    public void Named_targets_resolve_including_aliases(string typed, string commandId)
    {
        Assert.Equal(commandId, MnemonicGrammar.Parse(typed, _effective).Binding?.CommandId);
    }

    [Fact]
    public void Unknown_text_has_no_binding_and_says_so()
    {
        var intent = MnemonicGrammar.Parse("xq", _effective);

        Assert.Null(intent.Binding);
        Assert.Equal("No mnemonic 'xq'.", intent.Explanation);
    }

    [Fact]
    public void Bare_n_is_not_a_navigation_because_it_has_no_code()
    {
        var intent = MnemonicGrammar.Parse("n", _effective);

        Assert.Equal(MnemonicVerb.Run, intent.Verb);
        Assert.Null(intent.Binding);
    }

    [Fact]
    public void Candidates_for_a_prefix_list_the_navigation_targets_shortest_first()
    {
        var candidates = MnemonicGrammar.Candidates("ng", _effective);

        Assert.All(candidates, c => Assert.StartsWith("ng", c.Mnemonic, StringComparison.Ordinal));
        Assert.Equal("ngc1", candidates[0].Mnemonic);
    }

    [Fact]
    public void Every_default_mnemonic_is_accepted_by_the_key_router_table()
    {
        var table = MnemonicResolver.Resolve(NavigationDefaults.Assignments).BuildTable([KeyStroke.OfChar('p')]);

        Assert.NotNull(table);
        foreach (var assignment in _effective)
        {
            Assert.Equal(assignment.CommandId, table.Exact(assignment.Mnemonic)?.CommandId);
        }
    }

    [Fact]
    public void Navigation_with_an_unknown_code_keeps_the_verb_and_code_but_has_no_binding()
    {
        var intent = MnemonicGrammar.Parse("nzzzz", _effective);

        Assert.Equal(MnemonicVerb.Navigate, intent.Verb);
        Assert.Equal("zzzz", intent.Code);
        Assert.Null(intent.Binding);
        Assert.Contains("nzzzz", intent.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_text_is_a_run_with_no_binding()
    {
        var intent = MnemonicGrammar.Parse(string.Empty, _effective);

        Assert.Equal(MnemonicVerb.Run, intent.Verb);
        Assert.Null(intent.Binding);
    }

    [Fact]
    public void Matching_is_case_sensitive()
    {
        var typed = _effective[0].Mnemonic;
        var shifted = typed.ToUpperInvariant();
        Assert.SkipWhen(string.Equals(typed, shifted, StringComparison.Ordinal), "The first mnemonic has no letters to shift.");

        Assert.Null(MnemonicGrammar.Parse(shifted, _effective).Binding);
    }

    [Fact]
    public void An_empty_effective_list_binds_nothing_and_has_no_candidates()
    {
        Assert.Null(MnemonicGrammar.Parse("nrfdrm", []).Binding);
        Assert.Empty(MnemonicGrammar.Candidates("n", []));
    }

    [Fact]
    public void Candidates_for_empty_text_list_every_assignment_shortest_first()
    {
        var candidates = MnemonicGrammar.Candidates(string.Empty, _effective);

        Assert.Equal(_effective.Count, candidates.Count);
        Assert.Equal(candidates.OrderBy(a => a.Mnemonic.Length).ThenBy(a => a.Mnemonic, StringComparer.Ordinal).Select(a => a.Mnemonic), candidates.Select(a => a.Mnemonic));
    }

    [Fact]
    public void Candidates_for_text_nothing_starts_with_are_empty()
    {
        Assert.Empty(MnemonicGrammar.Candidates("zzzz", _effective));
    }

    [Fact]
    public void A_full_mnemonic_is_its_own_candidate()
    {
        var mnemonic = _effective[0].Mnemonic;

        Assert.Contains(MnemonicGrammar.Candidates(mnemonic, _effective), a => a.Mnemonic == mnemonic);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => MnemonicGrammar.Parse(null!, _effective));
        Assert.Throws<ArgumentNullException>(() => MnemonicGrammar.Parse("n", null!));
    }
}
