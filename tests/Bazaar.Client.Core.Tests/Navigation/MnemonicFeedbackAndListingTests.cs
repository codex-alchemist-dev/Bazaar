using Bazaar.Client.Core.Input;
using Bazaar.Client.Core.Navigation;

namespace Bazaar.Client.Core.Tests.Navigation;

public class MnemonicFeedbackAndListingTests
{
    private static readonly IReadOnlyList<MnemonicAssignment> Effective =
        MnemonicResolver.Resolve(NavigationDefaults.Assignments).Effective;

    [Fact]
    public void Pending_speaks_the_candidates_and_how_to_run_them()
    {
        var chord = new ChordState("n", []);
        var text = MnemonicFeedback.For(new RouteResult(RouteOutcome.Pending, InputMode.Normal, chord), Effective);

        Assert.NotNull(text);
        Assert.StartsWith("n. ", text);
        Assert.Contains("nrfdrm, RyuseiFuda Roadmap", text);
        Assert.EndsWith("Press Enter to run.", text);
    }

    [Fact]
    public void Pending_caps_the_spoken_hints_and_counts_the_rest()
    {
        var extra = Enumerable.Range(0, 8)
            .Select(i => new MnemonicAssignment($"mod.{i}", $"Extra {i}", $"qq{i}", MnemonicTier.Module, "module:x"));
        var all = Effective.Concat(extra).ToList();
        var chord = new ChordState("q", []);

        var text = MnemonicFeedback.For(new RouteResult(RouteOutcome.Pending, InputMode.Normal, chord), all);

        Assert.Contains($"and {8 - MnemonicFeedback.MaxSpokenHints} more", text);
    }

    [Fact]
    public void Executed_speaks_the_target_title()
    {
        var result = new RouteResult(RouteOutcome.Executed, InputMode.Normal, ChordState.Empty, null, NavigationDefaults.UnreadCommandId);

        Assert.Equal("Unread.", MnemonicFeedback.For(result, Effective));
    }

    [Theory]
    [InlineData(RouteOutcome.Cancelled, "Cancelled.")]
    [InlineData(RouteOutcome.NoMatch, "No mnemonic matches.")]
    [InlineData(RouteOutcome.NoTarget, "Nothing here takes text input.")]
    public void Outcomes_have_fixed_spoken_text(RouteOutcome outcome, string expected)
    {
        Assert.Equal(expected, MnemonicFeedback.For(new RouteResult(outcome, InputMode.Normal, ChordState.Empty), Effective));
    }

    [Fact]
    public void Passthrough_and_consumed_outcomes_are_silent()
    {
        Assert.Null(MnemonicFeedback.For(new RouteResult(RouteOutcome.Passthrough, InputMode.Normal, ChordState.Empty), Effective));
        Assert.Null(MnemonicFeedback.For(new RouteResult(RouteOutcome.Consumed, InputMode.Normal, ChordState.Empty), Effective));
    }

    [Fact]
    public void Listing_puts_navigation_first_and_marks_locks()
    {
        var assignments = Effective.Concat([new MnemonicAssignment("client.lock", "Locked", "ab", MnemonicTier.CompanyLocked, "company")]).ToList();

        var rows = MnemonicListing.Build(assignments);

        Assert.Equal(assignments.Count, rows.Count);
        Assert.True(rows[0].Mnemonic.StartsWith('n'));
        Assert.Contains(rows, r => r.Mnemonic == "ab" && r.Locked);
        Assert.Contains(rows, r => r.Mnemonic == "kanban" && r.Alias);
    }
}
