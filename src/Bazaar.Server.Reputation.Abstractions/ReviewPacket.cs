namespace Bazaar.Server.Reputation;

/// <summary>
/// What a reviewer sees (doc 11, "Review packet"). It has no member for the author's id, name, model badge or model
/// family, so blinding holds by construction.
/// </summary>
/// <param name="PacketId">Unique id of this packet.</param>
/// <param name="WorkItemRef">The work item (for example a Kanban card) under review.</param>
/// <param name="SubjectRef">The reviewed artefact (PR or card reference).</param>
/// <param name="ContentRef">Reference to the diff or file set; the content itself is not rewritten.</param>
/// <param name="Title">Title with the author's identity scrubbed.</param>
/// <param name="Description">Description with the author's identity scrubbed.</param>
/// <param name="RubricText">Rubric or question put to the reviewer, scrubbed.</param>
/// <param name="CreatedAt">When the packet was built.</param>
public sealed record ReviewPacket(
    Guid PacketId,
    string WorkItemRef,
    string SubjectRef,
    string ContentRef,
    string Title,
    string Description,
    string RubricText,
    DateTimeOffset CreatedAt);
