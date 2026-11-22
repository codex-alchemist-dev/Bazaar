using Bazaar.GitHub.Events;
using Bazaar.GitHub.Processing;

namespace Bazaar.GitHub.Tests.Processing;

/// <summary>Webhook body shapes to <see cref="GitHubEvent"/> (doc 21, Event mapping). Bodies are trimmed to the fields the parser reads.</summary>
public sealed class GitHubWebhookPayloadParserTests
{
    private static readonly DateTimeOffset _received = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> _members = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "boss" };

    private static GitHubEvent ParseOk(string type, string json)
    {
        var result = GitHubWebhookPayloadParser.Parse(type, "guid-1", json, _received, _members);
        return Assert.IsType<WebhookParseResult.Parsed>(result).Event;
    }

    [Fact]
    public void Pull_request_review_requested_reads_the_reviewer_and_pr_author()
    {
        var e = ParseOk("pull_request", """
            {"action": "review_requested", "requested_reviewer": {"login": "bob-bazaar[bot]"}, "sender": {"login": "boss"}, "repository": {"node_id": "R_1"},
             "pull_request": {"node_id": "PR_1", "body": "b", "updated_at": "2026-11-22T08:00:00Z", "head": {"sha": "abc"}, "user": {"login": "carol"}, "author_association": "NONE", "merged": false}}
            """);

        Assert.Equal(GitHubEventSource.Webhook, e.Source);
        Assert.Equal("guid-1", e.EventId);
        Assert.Equal("review_requested", e.Action);
        Assert.Equal("PR_1", e.PrNodeId);
        Assert.Equal("R_1", e.RepoNodeId);
        Assert.Equal("bob-bazaar[bot]", e.ReviewerLogin);
        Assert.Equal("carol", e.PrAuthorLogin);
        Assert.Equal("abc", e.HeadSha);
        Assert.Equal("MEMBER", e.AuthorAssociation);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero), e.UpdatedAt);
    }

    [Fact]
    public void Pull_request_closed_reads_merged()
    {
        var e = ParseOk("pull_request", """
            {"action": "closed", "sender": {"login": "boss"}, "repository": {"node_id": "R_1"}, "pull_request": {"node_id": "PR_1", "updated_at": "2026-11-22T08:00:00Z", "merged": true, "head": {"sha": "abc"}, "user": {"login": "boss"}}}
            """);

        Assert.True(e.Merged);
    }

    [Fact]
    public void Review_reads_lowercased_state_and_commit()
    {
        var e = ParseOk("pull_request_review", """
            {"action": "submitted", "sender": {"login": "dave"}, "repository": {"node_id": "R_1"},
             "review": {"node_id": "PRR_1", "state": "CHANGES_REQUESTED", "body": "fix", "commit_id": "def", "submitted_at": "2026-11-22T08:30:00Z", "user": {"login": "dave"}, "author_association": "COLLABORATOR"},
             "pull_request": {"node_id": "PR_1", "user": {"login": "bob-bazaar[bot]"}, "head": {"sha": "abc"}}}
            """);

        Assert.Equal("changes_requested", e.ReviewState);
        Assert.Equal("PRR_1", e.ObjectNodeId);
        Assert.Equal("def", e.HeadSha);
        Assert.Equal("COLLABORATOR", e.AuthorAssociation);
        Assert.Equal("bob-bazaar[bot]", e.PrAuthorLogin);
    }

    [Fact]
    public void Review_comment_belongs_to_the_pr()
    {
        var e = ParseOk("pull_request_review_comment", """
            {"action": "created", "sender": {"login": "dave"}, "repository": {"node_id": "R_1"},
             "comment": {"node_id": "PRRC_1", "body": "@bob-bazaar[bot] look", "updated_at": "2026-11-22T08:31:00Z", "user": {"login": "dave"}, "author_association": "MEMBER"},
             "pull_request": {"node_id": "PR_1", "head": {"sha": "abc"}}}
            """);

        Assert.Equal("PR_1", e.PrNodeId);
        Assert.Equal("PRRC_1", e.ObjectNodeId);
        Assert.Equal("@bob-bazaar[bot] look", e.Body);
    }

    [Fact]
    public void Issue_comment_on_a_pr_uses_the_pr_node_and_on_an_issue_the_issue_node()
    {
        var onPr = ParseOk("issue_comment", """
            {"action": "created", "sender": {"login": "dave"}, "repository": {"node_id": "R_1"},
             "issue": {"node_id": "PR_1", "pull_request": {"url": "u"}},
             "comment": {"node_id": "IC_1", "body": "x", "updated_at": "2026-11-22T08:00:00Z", "user": {"login": "dave"}, "author_association": "OWNER"}}
            """);
        var onIssue = ParseOk("issue_comment", """
            {"action": "created", "sender": {"login": "dave"}, "repository": {"node_id": "R_1"},
             "issue": {"node_id": "I_1"},
             "comment": {"node_id": "IC_2", "body": "x", "updated_at": "2026-11-22T08:00:00Z", "user": {"login": "dave"}, "author_association": "OWNER"}}
            """);

        Assert.Equal("PR_1", onPr.PrNodeId);
        Assert.Null(onPr.IssueNodeId);
        Assert.Null(onIssue.PrNodeId);
        Assert.Equal("I_1", onIssue.IssueNodeId);
    }

    [Fact]
    public void Issues_event_reads_the_issue()
    {
        var e = ParseOk("issues", """
            {"action": "opened", "sender": {"login": "eve"}, "repository": {"node_id": "R_1"},
             "issue": {"node_id": "I_1", "body": "hello", "updated_at": "2026-11-22T08:00:00Z", "user": {"login": "eve"}, "author_association": "NONE"}}
            """);

        Assert.Equal("I_1", e.IssueNodeId);
        Assert.Equal("NONE", e.AuthorAssociation);
    }

    [Fact]
    public void Check_run_and_suite_read_conclusion_and_head_sha()
    {
        var run = ParseOk("check_run", """
            {"action": "completed", "sender": {"login": "github-actions[bot]"}, "repository": {"node_id": "R_1"},
             "check_run": {"node_id": "CR_1", "conclusion": "failure", "head_sha": "abc", "completed_at": "2026-11-22T08:40:00Z"}}
            """);
        var suite = ParseOk("check_suite", """
            {"action": "completed", "sender": {"login": "github-actions[bot]"}, "repository": {"node_id": "R_1"},
             "check_suite": {"node_id": "CS_1", "conclusion": "timed_out", "head_sha": "abc", "updated_at": "2026-11-22T08:41:00Z"}}
            """);

        Assert.Equal("failure", run.Conclusion);
        Assert.Equal("abc", run.HeadSha);
        Assert.Equal("CR_1", run.ObjectNodeId);
        Assert.Equal("timed_out", suite.Conclusion);
        Assert.Null(run.PrNodeId);
    }

    [Fact]
    public void Push_reads_the_new_head_and_has_no_action()
    {
        var e = ParseOk("push", """
            {"ref": "refs/heads/x", "after": "0123abc", "action": "ignored", "sender": {"login": "bob-bazaar[bot]"}, "repository": {"node_id": "R_1"}}
            """);

        Assert.Equal("0123abc", e.HeadSha);
        Assert.Null(e.Action);
        Assert.Equal(_received, e.UpdatedAt);
    }

    [Fact]
    public void Check_events_read_the_listed_pull_request_numbers()
    {
        var run = ParseOk("check_run", """
            {"action": "completed", "sender": {"login": "github-actions[bot]"}, "repository": {"node_id": "R_1"},
             "check_run": {"node_id": "CR_1", "conclusion": "failure", "head_sha": "abc", "pull_requests": [{"number": 7}, {"number": 9}, {"number": 7}]}}
            """);
        var suite = ParseOk("check_suite", """
            {"action": "completed", "sender": {"login": "github-actions[bot]"}, "repository": {"node_id": "R_1"},
             "check_suite": {"node_id": "CS_1", "conclusion": "failure", "head_sha": "abc", "pull_requests": []}}
            """);

        Assert.Equal([7, 9], run.PullRequestNumbers);
        Assert.Empty(suite.PullRequestNumbers);
    }

    [Fact]
    public void A_branch_deleting_push_names_no_head()
    {
        var e = ParseOk("push", $$$"""
            {"ref": "refs/heads/x", "after": "{{{new string('0', 40)}}}", "sender": {"login": "bob-bazaar[bot]"}, "repository": {"node_id": "R_1"}}
            """);

        Assert.Null(e.HeadSha);
    }

    [Fact]
    public void Association_is_trusted_only_for_the_subject_author_or_a_member_login()
    {
        // The sender (frank) is not the comment author (dave), so dave's OWNER association does not carry over.
        var e = ParseOk("issue_comment", """
            {"action": "edited", "sender": {"login": "frank"}, "repository": {"node_id": "R_1"}, "issue": {"node_id": "I_1"},
             "comment": {"node_id": "IC_1", "user": {"login": "dave"}, "author_association": "OWNER"}}
            """);

        Assert.Equal("NONE", e.AuthorAssociation);
    }

    [Fact]
    public void Unsupported_event_types_are_ignored()
    {
        Assert.IsType<WebhookParseResult.Ignored>(GitHubWebhookPayloadParser.Parse("ping", "g", "{}", _received, _members));
        Assert.IsType<WebhookParseResult.Ignored>(GitHubWebhookPayloadParser.Parse("installation", "g", null, _received, _members));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"action": "opened"}""")]
    [InlineData("""{"repository": {"node_id": "R_1"}, "sender": {"login": "a"}, "action": "opened"}""")]
    public void Unusable_bodies_are_malformed(string? json)
    {
        Assert.IsType<WebhookParseResult.Malformed>(GitHubWebhookPayloadParser.Parse("pull_request", "g", json, _received, _members));
    }

    [Fact]
    public void Pull_request_fills_the_registration_fields_like_the_Events_API_parser()
    {
        var e = ParseOk("pull_request", """
            {"action": "opened", "sender": {"login": "boss"}, "repository": {"node_id": "R_1", "full_name": "codex-alchemist-dev/Bazaar"},
             "pull_request": {"node_id": "PR_1", "number": 12, "title": "Add wake", "state": "closed", "merged": true, "draft": false,
               "updated_at": "2026-11-22T08:00:00Z", "head": {"sha": "abc"}, "base": {"ref": "main"}, "user": {"login": "carol"},
               "assignees": [{"login": "bob-bazaar[bot]"}], "requested_reviewers": [{"login": "ada-bazaar[bot]"}, {"login": "BOB-bazaar[bot]"}]}}
            """);

        Assert.Equal(12, e.Number);
        Assert.Equal("Add wake", e.Title);
        Assert.Equal("merged", e.PrState);
        Assert.False(e.Draft);
        Assert.Equal("main", e.BaseRef);
        Assert.Equal("codex-alchemist-dev/Bazaar", e.RepoFullName);
        Assert.Equal(["bob-bazaar[bot]", "ada-bazaar[bot]"], e.AssigneeLogins);
    }

    [Fact]
    public void HeadRef_is_the_head_branch_only_for_a_same_repository_PR()
    {
        static string Pull(string headRepo) => $$$$"""
            {"action": "synchronize", "sender": {"login": "boss"}, "repository": {"node_id": "R_1", "full_name": "o/r"},
             "pull_request": {"node_id": "PR_1", "number": 12, "title": "t", "state": "open", "updated_at": "2026-11-22T08:00:00Z",
               "head": {"sha": "abc", "ref": "agent/wake", "repo": {"full_name": "{{{{headRepo}}}}"}}, "base": {"ref": "main", "repo": {"full_name": "O/R"}}, "user": {"login": "carol"}}}
            """;

        Assert.Equal("agent/wake", ParseOk("pull_request", Pull("o/r")).HeadRef);
        Assert.Null(ParseOk("pull_request", Pull("fork-owner/r")).HeadRef);
    }

    [Theory]
    [InlineData("refs/heads/agent/wake", "1111111111111111111111111111111111111111", "agent/wake")]
    [InlineData("refs/tags/v1", "1111111111111111111111111111111111111111", null)]
    [InlineData("refs/heads/agent/wake", "0000000000000000000000000000000000000000", null)]
    public void A_push_carries_its_branch_name_except_for_tags_and_deletions(string gitRef, string after, string? expected)
    {
        var e = ParseOk("push", $$$$"""{"ref": "{{{{gitRef}}}}", "after": "{{{{after}}}}", "sender": {"login": "boss"}, "repository": {"node_id": "R_1"}}""");

        Assert.Equal(expected, e.HeadRef);
    }

    [Fact]
    public void A_PR_comment_takes_number_title_and_state_from_the_issue()
    {
        var e = ParseOk("issue_comment", """
            {"action": "created", "sender": {"login": "boss"}, "repository": {"node_id": "R_1", "full_name": "o/r"},
             "issue": {"node_id": "PR_1", "number": 12, "title": "Add wake", "state": "open", "pull_request": {}},
             "comment": {"node_id": "IC_1", "body": "hi", "updated_at": "2026-11-22T08:30:00Z", "user": {"login": "boss"}}}
            """);

        Assert.Equal("PR_1", e.PrNodeId);
        Assert.Equal(12, e.Number);
        Assert.Equal("Add wake", e.Title);
        Assert.Equal("open", e.PrState);
        Assert.Null(e.Draft);
        Assert.Empty(e.AssigneeLogins);
    }
}
