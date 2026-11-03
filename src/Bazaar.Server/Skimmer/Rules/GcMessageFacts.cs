namespace Bazaar.Server.Skimmer.Rules;

/// <summary>The facts about one GC message that the rules stage needs for one agent (doc 13, "Placement").</summary>
/// <param name="GcId">The GC the message was posted in.</param>
/// <param name="Seq">The message's sequence number within its GC.</param>
/// <param name="AgentId">The agent whose wake decision is being made.</param>
/// <param name="AgentHandles">The handles that count as a mention of this agent.</param>
/// <param name="MentionedHandles">The handles mentioned in the message text.</param>
/// <param name="IsDmWithAgent">True when the GC is a two-member DM with this agent.</param>
/// <param name="IsOwnerAuthor">True when the owner wrote the message.</param>
/// <param name="IsCritical">True when the message is blocked, requests review, or calls a decision.</param>
/// <param name="SenderQuarantined">True when the sender is quarantined (doc 22).</param>
public sealed record GcMessageFacts(
    Guid GcId,
    long Seq,
    Guid AgentId,
    IReadOnlySet<string> AgentHandles,
    IReadOnlyList<string> MentionedHandles,
    bool IsDmWithAgent,
    bool IsOwnerAuthor,
    bool IsCritical,
    bool SenderQuarantined);
