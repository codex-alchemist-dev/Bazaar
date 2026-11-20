using Bazaar.Client.Core.Commands;

namespace Bazaar.Client.Core.Tests.Commands;

public class CommandMatcherTests
{
    private static Command Cmd(string id, string title, IEnumerable<string>? keywords = null, Func<bool>? enabled = null) =>
        new(id, title, id, keywords, CommandSource.Local, enabled);

    private static CommandRegistry Registry(params Command[] commands) =>
        CommandRegistry.Load(commands, []).Registry;

    [Fact]
    public void Non_matching_commands_are_excluded()
    {
        var registry = Registry(Cmd("a", "Open settings"), Cmd("b", "Close window"));

        var results = new CommandMatcher().Search(registry, "zzz");

        Assert.Empty(results);
    }

    [Fact]
    public void Title_match_is_preferred_over_keyword_match()
    {
        var registry = Registry(
            Cmd("byKeyword", "Unrelated", ["settings"]),
            Cmd("byTitle", "Settings"));

        var results = new CommandMatcher().Search(registry, "settings");

        Assert.Equal(["byTitle", "byKeyword"], results.Select(r => r.Command.Id));
        Assert.Equal(CommandMatchField.Title, results[0].Field);
        Assert.Equal(CommandMatchField.Keyword, results[1].Field);
    }

    [Fact]
    public void Keyword_match_is_preferred_over_id_match()
    {
        var registry = Registry(
            new Command("zeta", "Unrelated", "zeta", ["prefs"], CommandSource.Local),
            Cmd("prefs", "Other"));

        var results = new CommandMatcher().Search(registry, "prefs");

        Assert.Equal(["zeta", "prefs"], results.Select(r => r.Command.Id));
        Assert.Equal(CommandMatchField.Keyword, results[0].Field);
        Assert.Equal(CommandMatchField.Id, results[1].Field);
    }

    [Fact]
    public void Keyword_match_reports_the_keyword_and_its_positions()
    {
        var registry = Registry(new Command("x", "Unrelated", "x", ["user prefs"], CommandSource.Local));

        var match = Assert.Single(new CommandMatcher().Search(registry, "prf"));

        Assert.Equal("user prefs", match.MatchedText);
        Assert.Equal([5, 6, 8], match.Positions);
    }

    [Fact]
    public void Title_match_returns_title_positions_for_highlighting()
    {
        var registry = Registry(Cmd("open", "Open chat"));

        var match = Assert.Single(new CommandMatcher().Search(registry, "oc"));

        Assert.Equal("Open chat", match.MatchedText);
        Assert.Equal([0, 5], match.Positions);
    }

    [Fact]
    public void Ties_are_broken_by_title_ordinal()
    {
        var registry = Registry(Cmd("second", "Bravo"), Cmd("first", "Alpha"));

        var results = new CommandMatcher().Search(registry, "");

        Assert.Equal(["Alpha", "Bravo"], results.Select(r => r.Command.Title));
    }

    [Fact]
    public void Disabled_commands_are_excluded()
    {
        var registry = Registry(Cmd("on", "Open", enabled: () => true), Cmd("off", "Open all", enabled: () => false));

        var results = new CommandMatcher().Search(registry, "open");

        Assert.Equal(["on"], results.Select(r => r.Command.Id));
    }

    [Fact]
    public void Enabled_predicate_is_evaluated_at_query_time()
    {
        var enabled = false;
        var registry = Registry(Cmd("toggle", "Toggle panel", enabled: () => enabled));
        var matcher = new CommandMatcher();

        Assert.Empty(matcher.Search(registry, "panel"));
        enabled = true;
        Assert.Single(matcher.Search(registry, "panel"));
    }

    [Fact]
    public void Recent_command_is_boosted_within_its_tier()
    {
        var registry = Registry(Cmd("a", "Alpha tool"), Cmd("b", "Alpine tool"));
        var recency = new CommandRecency();
        var matcher = new CommandMatcher(recency);

        var before = matcher.Search(registry, "alp").Select(r => r.Command.Id).ToList();
        recency.Touch(before[1]);
        var after = matcher.Search(registry, "alp").Select(r => r.Command.Id).ToList();

        Assert.Equal(before[1], after[0]);
    }

    [Fact]
    public void Recency_is_a_small_boost_and_does_not_beat_a_clearly_better_match()
    {
        var registry = Registry(Cmd("exact", "Chat"), Cmd("loose", "Chart"));
        var recency = new CommandRecency();
        recency.Touch("loose");
        var matcher = new CommandMatcher(recency);

        var results = matcher.Search(registry, "chat");

        Assert.Equal("exact", results[0].Command.Id);
    }

    [Fact]
    public void Without_recency_ordering_is_stable_across_calls()
    {
        var registry = Registry(Cmd("b", "Beta one"), Cmd("a", "Alpha one"));
        var matcher = new CommandMatcher();

        var first = matcher.Search(registry, "one").Select(r => r.Command.Id).ToList();
        var second = matcher.Search(registry, "one").Select(r => r.Command.Id).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Blank_query_lists_every_enabled_command()
    {
        var registry = Registry(Cmd("a", "Alpha"), Cmd("b", "Beta", enabled: () => false));

        var results = new CommandMatcher().Search(registry, "");

        Assert.Equal(["a"], results.Select(r => r.Command.Id));
        Assert.All(results, r => Assert.Empty(r.Positions));
    }
}
