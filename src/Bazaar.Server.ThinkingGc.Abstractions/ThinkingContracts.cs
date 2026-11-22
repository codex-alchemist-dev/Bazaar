namespace Bazaar.Server.ThinkingGc;

/// <summary>What a Thinking GC entry records about a turn (docs/design/01-gc-data-model.md, Thinking GCs).</summary>
public enum ThinkingEntryKind
{
    /// <summary>The model's reasoning text for one model step.</summary>
    Reasoning,

    /// <summary>One tool step: the tool name and how the call ended. Arguments and results are not copied here.</summary>
    Tool,

    /// <summary>The turn's end: how it finished.</summary>
    TurnEnd,
}

/// <summary>
/// One entry for a Thinking GC. <see cref="WorkingGcId"/> is the GC the agent is working in; the entry lands in the agent's
/// Thinking GC nested under it. <see cref="Ordinal"/> is the entry's number within the turn and, with the turn id and kind,
/// makes a repeated entry a no-op.
/// </summary>
/// <param name="AgentId">The agent whose turn this is.</param>
/// <param name="TurnId">The turn.</param>
/// <param name="WorkingGcId">The GC that woke the turn, or null when the turn has no working GC (then nothing is recorded).</param>
/// <param name="Ordinal">Entry number within the turn, from 1.</param>
/// <param name="Kind">What the entry holds.</param>
/// <param name="Text">The entry text. Never empty.</param>
public sealed record ThinkingEntry(Guid AgentId, Guid TurnId, string? WorkingGcId, int Ordinal, ThinkingEntryKind Kind, string Text);

/// <summary>Where a turn's thinking and steps go (doc 01 Thinking GCs; doc 06 Thinking GCs).</summary>
public interface IThinkingRecorder
{
    /// <summary>
    /// Writes the entry into the agent's Thinking GC under the working GC, creating that GC first when it does not exist. Idempotent
    /// on (turn, ordinal, kind). Returns false when nothing was written: no working GC, the working GC is archived or is itself a
    /// Thinking GC, or the agent is not an active member of it.
    /// </summary>
    Task<bool> RecordAsync(ThinkingEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Tells other modules whether a GC is a Thinking GC (doc 01 Thinking GCs). The Archive and cold-storage modules refuse such
/// text. A GC that does not exist is not a Thinking GC.
/// </summary>
public interface IThinkingGcLookup
{
    /// <summary>True when the GC exists and has type <c>thinking</c>.</summary>
    Task<bool> IsThinkingGcAsync(Guid gcId, CancellationToken cancellationToken);
}

/// <summary>
/// Whether creating a Thinking GC also posts one collapsed pointer event in the working GC (doc 01 "The parent shows one
/// collapsed pointer event" [PROPOSED]). Off by default.
/// </summary>
public interface IThinkingPointerPolicy
{
    /// <summary>True when the pointer event is posted.</summary>
    ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken);
}
