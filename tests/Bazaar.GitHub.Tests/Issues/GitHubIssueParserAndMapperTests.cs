using Bazaar.GitHub.Events;
using Bazaar.GitHub.Processing;

namespace Bazaar.GitHub.Tests.Issues;

/// <summary>Issue fields from both parsers, and the issue assignment mapping (doc 21, Issue GCs).</summary>
public sealed class GitHubIssueParserAndMapperTests
{
    private const string _agent = "bob-bazaar[bot]";
    private static readonly DateTimeOffset _received = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> _members = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "boss" };

    private const string _assigned = """
        {"action": "assigned", "sender": {"login": "boss"}, "assignee": {"login": "bob-bazaar[bot]"}, "repository": {"node_id": "R_1", "full_name": "o/r"},
         "issue": {"node_id": "I_1", "number": 12, "title": "Crash on start", "state": "OPEN", "body": "b", "updated_at": "2026-11-22T08:00:00Z",
                   "user": {"login": "carol"}, "assignees": [{"login": "bob-bazaar[bot]"}, {"login": "dave"}]}}
        """;

    [Fact]
    public void Webhook_parser_reads_the_issue_fields()
    {
        var e = Assert.IsType<WebhookParseResult.Parsed>(GitHubWebhookPayloadParser.Parse("issues", "g1", _assigned, _received, _members)).Event;

        Assert.Equal("I_1", e.IssueNodeId);
        Assert.Null(e.PrNodeId);
        Assert.Equal(12, e.IssueNumber);
        Assert.Equal("Crash on start", e.IssueTitle);
        Assert.Equal("open", e.IssueState);
        Assert.Equal("carol", e.IssueAuthorLogin);
        Assert.Equal(_agent, e.AssignedLogin);
        Assert.Equal([_agent, "dave"], e.IssueAssigneeLogins);
    }

    [Fact]
    public void Events_API_parser_reads_the_issue_fields()
    {
        const string json = """
            {"id": "99", "type": "IssuesEvent", "actor": {"login": "boss"}, "repo": {"id": 1, "name": "o/r"}, "created_at": "2026-11-22T08:00:00Z",
             "payload": {"action": "assigned", "assignee": {"login": "bob-bazaar[bot]"},
               "issue": {"node_id": "I_1", "number": 12, "title": "Crash", "state": "closed", "updated_at": "2026-11-22T08:00:00Z", "user": {"login": "carol"}, "assignees": [{"login": "dave"}]}}}
            """;
        var result = GitHubEventsApiParser.Parse($"[{json}]", "R_1", _members);

        var e = Assert.Single(result.Events);
        Assert.Equal("I_1", e.IssueNodeId);
        Assert.Equal(12, e.IssueNumber);
        Assert.Equal("closed", e.IssueState);
        Assert.Equal("carol", e.IssueAuthorLogin);
        Assert.Equal(_agent, e.AssignedLogin);
        Assert.Equal(["dave"], e.IssueAssigneeLogins);
    }

    [Fact]
    public void A_member_assigning_an_agent_writes_an_issue_entry_and_wakes_it()
    {
        var e = Assert.IsType<WebhookParseResult.Parsed>(GitHubWebhookPayloadParser.Parse("issues", "g1", _assigned, _received, _members)).Event;

        var effect = Assert.Single(new GitHubEventMapper([_agent]).Map(e));

        Assert.Equal(GcTargetKind.Issue, effect.Entry!.Target.Kind);
        Assert.Equal("I_1", effect.Entry.Target.NodeId);
        Assert.True(effect.Wakes);
        var item = Assert.Single(effect.InboxItems);
        Assert.Equal(_agent, item.RecipientLogin);
        Assert.Equal(InboxKind.Assignment, item.Kind);
    }

    [Fact]
    public void Assigning_a_person_writes_the_entry_without_a_wake()
    {
        var e = Assert.IsType<WebhookParseResult.Parsed>(GitHubWebhookPayloadParser.Parse("issues", "g1", _assigned.Replace("bob-bazaar[bot]\"}, \"repository", "dave\"}, \"repository"), _received, _members)).Event;

        var effect = Assert.Single(new GitHubEventMapper([_agent]).Map(e));

        Assert.False(effect.Wakes);
        Assert.NotNull(effect.Entry);
    }
}
