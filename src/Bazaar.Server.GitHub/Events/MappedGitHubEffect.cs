namespace Bazaar.Server.GitHub.Events;

/// <summary>Whether the event's author is a trusted member. Non-members are external content.</summary>
public enum GitHubProvenance
{
    Member,
    External,
}

/// <summary>Which GC an entry belongs to. Inbox entries have no node id.</summary>
public enum GcTargetKind
{
    PullRequest,
    Issue,
    Inbox,
}

/// <summary>The GC an entry draft is written to. <see cref="NodeId"/> is the PR or issue node id, null for the inbox GC.</summary>
public sealed record GcTarget(GcTargetKind Kind, string? NodeId);

/// <summary>A GC entry to write later (doc 01 model; this package writes nothing).</summary>
/// <param name="BodyExcerpt">A short excerpt of a member's body. Always null for external content.</param>
public sealed record GcEntryDraft(GcTarget Target, string Kind, string Summary, string? BodyExcerpt);

/// <summary>Who an inbox item is for. <see cref="Owner"/> is the human owner, never an agent.</summary>
public enum InboxRecipientRole
{
    Assignee,
    Author,
    Mentioned,
    Owner,
}

/// <summary>Inbox item kinds. The first three are the doc 21 mapping kinds; the last two cover author-side rows.</summary>
public enum InboxKind
{
    Assignment,
    Mention,
    OwnerNotice,
    ReviewFeedback,
    CheckFailure,
}

/// <summary>An inbox item to enqueue later (doc 06 owns the table). <see cref="Wakes"/> says whether it starts a turn.</summary>
public sealed record InboxItemDraft(InboxRecipientRole RecipientRole, string? RecipientLogin, InboxKind Kind, bool Wakes);

/// <summary>
/// One effect of a GitHub event. An external effect is hard-tainted and never wakes an agent
/// (doc 21, Event mapping; doc 15). It carries no raw body text.
/// </summary>
public sealed record MappedGitHubEffect(GcEntryDraft? Entry, IReadOnlyList<InboxItemDraft> InboxItems, GitHubProvenance Provenance)
{
    public bool HardTaint => Provenance == GitHubProvenance.External;

    /// <summary>True only for member-authored effects with at least one waking inbox item.</summary>
    public bool Wakes => Provenance == GitHubProvenance.Member && InboxItems.Any(item => item.Wakes);
}
