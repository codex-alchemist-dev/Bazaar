namespace Bazaar.Server.GitHub.Events;

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
}
