using Bazaar.Client.Core.Input;
using Bazaar.Client.Components.Navigation;

namespace Bazaar.Client.Components.Navigation.Tests;

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
}
