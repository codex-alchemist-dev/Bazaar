namespace Bazaar.Reputation;

/// <summary>The author identity that a review packet must not carry. It stays in the GC and the review row.</summary>
/// <param name="AgentId">The author's agent id.</param>
/// <param name="DisplayName">The author's display name.</param>
/// <param name="ModelBadge">The model badge string shown in the GC (for example "Haiku 5.5").</param>
/// <param name="ModelFamily">The model family, or null when unknown.</param>
public sealed record AuthorIdentity(string AgentId, string DisplayName, string ModelBadge, string? ModelFamily = null);

/// <summary>Builder input: the review material plus the author's identity, kept separate from it.</summary>
/// <param name="WorkItemRef">The work item under review.</param>
/// <param name="SubjectRef">The reviewed PR or card.</param>
/// <param name="ContentRef">Reference to the diff or file set.</param>
/// <param name="Title">Free-text title; may mention the author.</param>
/// <param name="Description">Free-text description; may mention the author.</param>
/// <param name="RubricText">Rubric or question for the reviewer.</param>
/// <param name="Author">The author's identity, used only to scrub the text.</param>
public sealed record ReviewSubject(
    string WorkItemRef,
    string SubjectRef,
    string ContentRef,
    string Title,
    string Description,
    string RubricText,
    AuthorIdentity Author);

/// <summary>A built packet and how many identity strings were scrubbed from its text (for the server's audit trail only).</summary>
/// <param name="Packet">The blinded packet.</param>
/// <param name="RedactionCount">Number of replacements made across the text fields.</param>
public sealed record ReviewPacketResult(ReviewPacket Packet, int RedactionCount);
