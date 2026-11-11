using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Gc.Mentions;

namespace Bazaar.Server.Tests.Agents.Turns.Posting;

/// <summary>Pure tests for the agent mention scanner: spans, UTF-16 offsets, skipped code, broadcasts.</summary>
public sealed class AgentMentionParserTests
{
    private static readonly Guid _ana = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _bobSmith = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly GcMentionMember[] _members =
    [
        new GcMentionMember(_ana, "ana"),
        new GcMentionMember(_bob, "bob"),
    ];

    private static readonly string[] _broadcastsHereAll = ["here", "all"];
    private static readonly string[] _all = ["all"];

    private static AgentMentionScan Scan(string text, params GcMentionMember[] members) =>
        AgentMentionParser.Scan(text, members.Length == 0 ? _members : members);

    [Fact]
    public void A_mention_at_the_start_of_the_text_is_found_with_its_at_sign()
    {
        var scan = Scan("@ana hello");

        Assert.Equal(new MentionSpan(0, 4, _ana), Assert.Single(scan.Spans));
        Assert.Empty(scan.Broadcasts);
    }

    [Fact]
    public void A_mention_after_whitespace_is_found()
    {
        var scan = Scan("hi\n\t@bob");

        Assert.Equal(new MentionSpan(4, 4, _bob), Assert.Single(scan.Spans));
    }

    [Fact]
    public void A_mention_after_opening_punctuation_is_found()
    {
        var scan = Scan("see (@ana) and [@bob]");

        Assert.Equal(
            new[] { new MentionSpan(5, 4, _ana), new MentionSpan(16, 4, _bob) },
            scan.Spans);
    }

    [Fact]
    public void A_mention_followed_by_trailing_punctuation_stops_at_the_handle()
    {
        var scan = Scan("Ask @ana, then @bob. Thanks!");

        Assert.Equal(new[] { new MentionSpan(4, 4, _ana), new MentionSpan(15, 4, _bob) }, scan.Spans);
    }

    [Fact]
    public void An_email_like_address_is_not_a_mention()
    {
        var scan = Scan("write to ana@bob.example or bob@ana");

        Assert.Empty(scan.Spans);
    }

    [Fact]
    public void A_handle_that_runs_into_more_letters_is_not_a_mention()
    {
        var scan = Scan("@anas is not @ana");

        Assert.Equal(new MentionSpan(13, 4, _ana), Assert.Single(scan.Spans));
    }

    [Fact]
    public void Text_inside_a_code_span_is_not_scanned()
    {
        var scan = Scan("use `@ana` but not ``@bob`` and @ana");

        Assert.Equal(new MentionSpan(32, 4, _ana), Assert.Single(scan.Spans));
    }

    [Fact]
    public void An_unclosed_backtick_is_literal_and_the_mention_after_it_is_found()
    {
        var scan = Scan("a ` then @ana");

        Assert.Equal(new MentionSpan(9, 4, _ana), Assert.Single(scan.Spans));
    }

    [Fact]
    public void Text_inside_a_fenced_code_block_is_not_scanned_and_text_after_it_is()
    {
        var text = "start\n```\n@ana in code\n```\n@bob after";

        var scan = Scan(text);

        Assert.Equal(new MentionSpan(text.IndexOf("@bob", StringComparison.Ordinal), 4, _bob), Assert.Single(scan.Spans));
    }

    [Fact]
    public void An_unclosed_fenced_block_runs_to_the_end_of_the_text()
    {
        var scan = Scan("```\n@ana never closed");

        Assert.Empty(scan.Spans);
    }

    [Fact]
    public void The_longest_matching_handle_wins()
    {
        var members = new[]
        {
            new GcMentionMember(_bob, "bob"),
            new GcMentionMember(_bobSmith, "bob.smith"),
        };

        var scan = Scan("hi @bob.smith and @bob", members);

        Assert.Equal(
            new[] { new MentionSpan(3, 10, _bobSmith), new MentionSpan(18, 4, _bob) },
            scan.Spans);
    }

    [Fact]
    public void Handles_match_case_insensitively()
    {
        var scan = Scan("Hello @ANA and @Bob");

        Assert.Equal(new[] { new MentionSpan(6, 4, _ana), new MentionSpan(15, 4, _bob) }, scan.Spans);
    }

    [Fact]
    public void An_unknown_handle_produces_no_span()
    {
        var scan = Scan("ping @carl and @nobody");

        Assert.Empty(scan.Spans);
        Assert.Empty(scan.Broadcasts);
    }

    [Fact]
    public void Offsets_count_a_surrogate_pair_as_two_utf16_units()
    {
        // The emoji is two UTF-16 units; the mention starts after it and the space.
        var text = "\U0001F600 @ana";

        var scan = Scan(text);

        Assert.Equal(3, text.IndexOf('@'));
        Assert.Equal(new MentionSpan(3, 4, _ana), Assert.Single(scan.Spans));
        Assert.Equal("@ana", text.Substring(scan.Spans[0].Start, scan.Spans[0].Length));
    }

    [Fact]
    public void A_mention_directly_after_a_surrogate_pair_is_not_a_mention()
    {
        // The low surrogate is neither whitespace nor opening punctuation, so the "@" is not a mention start.
        var scan = Scan("\U0001F600@ana");

        Assert.Empty(scan.Spans);
    }

    [Fact]
    public void Broadcasts_are_reported_separately_and_never_become_spans()
    {
        var scan = Scan("@here please, and @all of you. @everyone is unknown");

        Assert.Empty(scan.Spans);
        Assert.Equal(_broadcastsHereAll, scan.Broadcasts);
    }

    [Fact]
    public void A_broadcast_next_to_a_member_mention_is_reported_and_the_member_is_still_a_span()
    {
        var scan = Scan("@all and @ana");

        Assert.Equal(new MentionSpan(9, 4, _ana), Assert.Single(scan.Spans));
        Assert.Equal(_all, scan.Broadcasts);
    }

    [Fact]
    public void A_broadcast_inside_a_code_span_is_not_reported()
    {
        var scan = Scan("`@here` only");

        Assert.Empty(scan.Broadcasts);
    }
}
