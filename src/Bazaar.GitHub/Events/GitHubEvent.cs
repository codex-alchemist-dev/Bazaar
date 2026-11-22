namespace Bazaar.GitHub.Events;

/// <summary>Where an event came from. Webhook delivery ids and poll event ids are separate namespaces.</summary>
public enum GitHubEventSource
{
    Webhook,
    Poll,
}

/// <summary>
/// One GitHub event after webhook verification or as returned by the Events API poller (doc 21, Event mapping).
/// Pure data: the mapper and dedupe helpers read it and never do I/O.
/// </summary>
public sealed record GitHubEvent
{
    public required GitHubEventSource Source { get; init; }

    /// <summary>GitHub event name, for example <c>pull_request</c>, <c>pull_request_review</c>, <c>issue_comment</c>, <c>check_run</c>, <c>check_suite</c>, <c>push</c>.</summary>
    public required string EventName { get; init; }

    /// <summary>The event action, for example <c>review_requested</c>, <c>closed</c>, <c>completed</c>. Null for <c>push</c>.</summary>
    public string? Action { get; init; }

    /// <summary>X-GitHub-Delivery for webhooks, the Events API event id for polls.</summary>
    public required string EventId { get; init; }

    public required string RepoNodeId { get; init; }

    public string? PrNodeId { get; init; }

    public string? IssueNodeId { get; init; }

    /// <summary>Node id of the comment, review or check run. Stable across webhook and poll, so it identifies one object.</summary>
    public string? ObjectNodeId { get; init; }

    public string? HeadSha { get; init; }

    public required string AuthorLogin { get; init; }

    /// <summary>GitHub's author_association: MEMBER, OWNER, COLLABORATOR, CONTRIBUTOR, FIRST_TIME_CONTRIBUTOR, NONE and so on.</summary>
    public required string AuthorAssociation { get; init; }

    /// <summary>Raw body text. The mapper never copies it into an actor-facing field for non-members.</summary>
    public string? Body { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>pull_request_review: <c>approved</c>, <c>changes_requested</c> or <c>commented</c>.</summary>
    public string? ReviewState { get; init; }

    /// <summary>check_run or check_suite: <c>success</c>, <c>failure</c>, <c>timed_out</c> and so on.</summary>
    public string? Conclusion { get; init; }

    /// <summary>pull_request review_requested: the login that was asked to review.</summary>
    public string? ReviewerLogin { get; init; }

    /// <summary>The PR author login, needed to route review feedback and check failures.</summary>
    public string? PrAuthorLogin { get; init; }

    /// <summary>pull_request closed: true when the PR was merged rather than closed.</summary>
    public bool Merged { get; init; }

    /// <summary>The PR number (or the issue number of a PR comment). Needed to register the PR; null when the source did not carry it.</summary>
    public int? Number { get; init; }

    /// <summary>The PR title, when the event carries the pull request object.</summary>
    public string? Title { get; init; }

    /// <summary>The PR state from the payload: <c>open</c>, <c>closed</c> or <c>merged</c>. Null when the event does not carry it.</summary>
    public string? PrState { get; init; }

    public bool? Draft { get; init; }

    public string? BaseRef { get; init; }

    /// <summary>
    /// The head branch name (PROPOSED, doc 21 PR lookup). On a pull request object: <c>head.ref</c>, only for a same-repository PR (null for a fork).
    /// On <c>push</c>: the branch from <c>refs/heads/&lt;name&gt;</c> (null for tags and branch deletions). Used to match a push to its PR before the head SHA is stored.
    /// </summary>
    public string? HeadRef { get; init; }

    /// <summary><c>owner/name</c> of the repository, used for the GC title only. Keys are node ids.</summary>
    public string? RepoFullName { get; init; }

    /// <summary>check_run or check_suite: the numbers of the PRs the payload lists in <c>pull_requests</c> (empty for forks and for other events). Used only to look the PR up.</summary>
    public IReadOnlyList<int> PullRequestNumbers { get; init; } = [];

    /// <summary>Logins assigned to or asked to review the PR, from the pull request object.</summary>
    public IReadOnlyList<string> AssigneeLogins { get; init; } = [];

    /// <summary>The issue number, for an <c>issues</c> or non-PR <c>issue_comment</c> event. Needed to register the issue GC (doc 21, issue GC).</summary>
    public int? IssueNumber { get; init; }

    /// <summary>The issue title, when the event carries the issue object.</summary>
    public string? IssueTitle { get; init; }

    /// <summary>The issue state from the payload, <c>open</c> or <c>closed</c>. Null when the event does not carry it.</summary>
    public string? IssueState { get; init; }

    /// <summary>The issue author login (<c>issue.user.login</c>), who is a member of the issue GC when it is an agent.</summary>
    public string? IssueAuthorLogin { get; init; }

    /// <summary>Logins assigned to the issue, from the issue object.</summary>
    public IReadOnlyList<string> IssueAssigneeLogins { get; init; } = [];

    /// <summary>issues assigned: the login that was just assigned (<c>assignee.login</c>).</summary>
    public string? AssignedLogin { get; init; }
}
