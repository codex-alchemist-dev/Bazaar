namespace Bazaar.Server.Skimmer.Digest;

/// <summary>
/// The Skimmer gate's verdict for one (message, member agent) pair (doc 13, "Behaviour and edge cases").
/// </summary>
/// <param name="AgentId">The member agent the decision is for.</param>
/// <param name="GcId">The GC the message was posted in.</param>
/// <param name="Seq">The per-GC sequence number of the message; 1 or more.</param>
/// <param name="Path">One of "mention", "dm", "critical", "quarantine", "scored" or "default".</param>
/// <param name="Decision">Either "wake" or "defer".</param>
/// <param name="Category">The message category, when the gate has one.</param>
/// <param name="ScoreBasisPoints">The relevance score in basis points, when the scored path ran.</param>
/// <param name="Shadow">True when the decision is recorded only (shadow mode).</param>
public sealed record GateDecision(
    Guid AgentId,
    Guid GcId,
    long Seq,
    string Path,
    string Decision,
    string? Category,
    int? ScoreBasisPoints,
    bool Shadow);
