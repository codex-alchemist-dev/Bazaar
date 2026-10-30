using System.Text.RegularExpressions;

namespace Bazaar.Server.GitHub.Events;

/// <summary>
/// Maps GitHub events to GC entry drafts and inbox item drafts (doc 21, Event mapping). Pure: no I/O, no clock, no database.
/// Non-member content (issues, comments, reviews) becomes an external effect with hard taint and no wake.
/// Events not listed in the mapping produce no effects.
/// </summary>
public sealed partial class GitHubEventMapper
{
    private const int _excerptLength = 200;

    private static readonly HashSet<string> _trustedAssociations = new(StringComparer.OrdinalIgnoreCase)
    {
        "MEMBER",
        "OWNER",
        "COLLABORATOR",
    };

    // Events whose body or text is authored by a person. Check runs and pushes are not content.
    private static readonly HashSet<string> _contentEventNames = new(StringComparer.Ordinal)
    {
        "issues",
        "issue_comment",
        "pull_request",
        "pull_request_review",
        "pull_request_review_comment",
    };

    private readonly HashSet<string> _agentLogins;

    /// <param name="agentLogins">GitHub logins of the agent Apps (for example <c>name-bazaar[bot]</c>).</param>
    public GitHubEventMapper(IEnumerable<string> agentLogins)
    {
        ArgumentNullException.ThrowIfNull(agentLogins);
        _agentLogins = new HashSet<string>(agentLogins, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True for MEMBER, OWNER and COLLABORATOR. Anything else, including a missing value, is not trusted.</summary>
    public static bool IsTrustedAssociation(string? association) =>
        association is not null && _trustedAssociations.Contains(association);

    /// <summary>Returns zero or more effects for one event. Each effect is one row of the doc 21 mapping table.</summary>
    public IReadOnlyList<MappedGitHubEffect> Map(GitHubEvent gitHubEvent)
    {
        ArgumentNullException.ThrowIfNull(gitHubEvent);

        // A non-member's content is handled first, so it can never produce a PR entry or a wake.
        if (_contentEventNames.Contains(gitHubEvent.EventName) && !IsTrustedAssociation(gitHubEvent.AuthorAssociation))
        {
            return MapExternal(gitHubEvent);
        }

        return gitHubEvent.EventName switch
        {
            "pull_request" => MapPullRequest(gitHubEvent),
            "pull_request_review" => MapReview(gitHubEvent),
            "issue_comment" or "pull_request_review_comment" or "issues" => MapComment(gitHubEvent),
            "check_run" or "check_suite" => MapCheck(gitHubEvent),
            "push" => MapPush(gitHubEvent),
            _ => [],
        };
    }

    /// <summary>Agent logins that are @-mentioned in <paramref name="body"/>, using the canonical spelling from the agent list.</summary>
    public IReadOnlyList<string> AgentMentions(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        var found = new List<string>();
        foreach (Match match in MentionPattern().Matches(body))
        {
            if (_agentLogins.TryGetValue(match.Groups[1].Value, out var canonical) && !found.Contains(canonical, StringComparer.Ordinal))
            {
                found.Add(canonical);
            }
        }

        return found;
    }

    private IReadOnlyList<MappedGitHubEffect> MapExternal(GitHubEvent e)
    {
        // Digest only: the entry is written to the inbox GC with no excerpt. Mentions of agents are stored as non-waking items.
        var entry = new GcEntryDraft(
            new GcTarget(GcTargetKind.Inbox, null),
            "external_content",
            $"Non-member {e.EventName} content withheld; reader summary only",
            BodyExcerpt: null);

        var items = AgentMentions(e.Body)
            .Select(agent => new InboxItemDraft(InboxRecipientRole.Mentioned, agent, InboxKind.Mention, Wakes: false))
            .ToList();

        return [new MappedGitHubEffect(entry, items, GitHubProvenance.External)];
    }

    private IReadOnlyList<MappedGitHubEffect> MapPullRequest(GitHubEvent e)
    {
        if (e.PrNodeId is null)
        {
            return [];
        }

        var target = new GcTarget(GcTargetKind.PullRequest, e.PrNodeId);

        switch (e.Action)
        {
            case "review_requested":
                {
                    var reviewer = e.ReviewerLogin;
                    var entry = new GcEntryDraft(target, "pr_event", $"Review requested from {reviewer}", BodyExcerpt: null);
                    if (reviewer is not null && IsAgent(reviewer))
                    {
                        var assignment = new InboxItemDraft(InboxRecipientRole.Assignee, reviewer, InboxKind.Assignment, Wakes: true);
                        return [new MappedGitHubEffect(entry, [assignment], GitHubProvenance.Member)];
                    }

                    return [new MappedGitHubEffect(entry, [], GitHubProvenance.Member)];
                }

            case "closed":
                {
                    var summary = e.Merged ? "PR merged" : "PR closed";
                    var entry = new GcEntryDraft(target, "pr_event", summary, BodyExcerpt: null);
                    // The owner gets the notice; no agent wakes. The outcome itself goes to doc 11 (not this mapper).
                    var notice = new InboxItemDraft(InboxRecipientRole.Owner, null, InboxKind.OwnerNotice, Wakes: false);
                    return [new MappedGitHubEffect(entry, [notice], GitHubProvenance.Member)];
                }

            default:
                return [];
        }
    }

    private IReadOnlyList<MappedGitHubEffect> MapReview(GitHubEvent e)
    {
        if (e.PrNodeId is null || e.ReviewState != "changes_requested")
        {
            return [];
        }

        var target = new GcTarget(GcTargetKind.PullRequest, e.PrNodeId);
        var entry = new GcEntryDraft(target, "pr_event", $"Changes requested by {e.AuthorLogin}", BodyExcerpt: null);
        var items = new List<InboxItemDraft>();
        if (e.PrAuthorLogin is not null && IsAgent(e.PrAuthorLogin))
        {
            items.Add(new InboxItemDraft(InboxRecipientRole.Author, e.PrAuthorLogin, InboxKind.ReviewFeedback, Wakes: true));
        }

        return [new MappedGitHubEffect(entry, items, GitHubProvenance.Member)];
    }

    private IReadOnlyList<MappedGitHubEffect> MapComment(GitHubEvent e)
    {
        // A PR comment goes to the PR GC; an issue comment goes to the issue's GC.
        GcTarget target;
        string kind;
        if (e.PrNodeId is not null)
        {
            target = new GcTarget(GcTargetKind.PullRequest, e.PrNodeId);
            kind = "pr_event";
        }
        else if (e.IssueNodeId is not null)
        {
            target = new GcTarget(GcTargetKind.Issue, e.IssueNodeId);
            kind = "issue_event";
        }
        else
        {
            return [];
        }

        var mentions = AgentMentions(e.Body);
        if (mentions.Count == 0)
        {
            return [];
        }

        var entry = new GcEntryDraft(target, kind, $"Comment mentions {string.Join(", ", mentions)}", Excerpt(e.Body));
        var items = mentions
            .Select(agent => new InboxItemDraft(InboxRecipientRole.Mentioned, agent, InboxKind.Mention, Wakes: true))
            .ToList();

        return [new MappedGitHubEffect(entry, items, GitHubProvenance.Member)];
    }

    private IReadOnlyList<MappedGitHubEffect> MapCheck(GitHubEvent e)
    {
        if (e.PrNodeId is null || e.Conclusion is not ("failure" or "timed_out"))
        {
            return [];
        }

        var target = new GcTarget(GcTargetKind.PullRequest, e.PrNodeId);
        var entry = new GcEntryDraft(target, "pr_event", $"{e.EventName} ended with {e.Conclusion}", BodyExcerpt: null);
        var items = new List<InboxItemDraft>();
        if (e.PrAuthorLogin is not null && IsAgent(e.PrAuthorLogin))
        {
            items.Add(new InboxItemDraft(InboxRecipientRole.Author, e.PrAuthorLogin, InboxKind.CheckFailure, Wakes: true));
        }

        return [new MappedGitHubEffect(entry, items, GitHubProvenance.Member)];
    }

    private static IReadOnlyList<MappedGitHubEffect> MapPush(GitHubEvent e)
    {
        // The caller resolves the PR for an agent branch; a push with no PR is not mapped.
        if (e.PrNodeId is null)
        {
            return [];
        }

        var entry = new GcEntryDraft(
            new GcTarget(GcTargetKind.PullRequest, e.PrNodeId),
            "pr_event",
            $"Pushed {e.HeadSha ?? "unknown head"}",
            BodyExcerpt: null);

        return [new MappedGitHubEffect(entry, [], GitHubProvenance.Member)];
    }

    private bool IsAgent(string login) => _agentLogins.Contains(login);

    private static string? Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= _excerptLength ? trimmed : trimmed[.._excerptLength];
    }

    // An @login: not preceded by a login character (so e-mail addresses do not match), with an optional [bot] suffix.
    [GeneratedRegex(@"(?<![A-Za-z0-9-])@([A-Za-z0-9][A-Za-z0-9-]{0,38}(?:\[bot\])?)", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();
}
