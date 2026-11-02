using System.Text.Json.Serialization;
using Bazaar.Contracts.Gc;

namespace Bazaar.Contracts.Soul;

/// <summary>The kind of personality-driven judgment a decision record captures (docs/design/07-soul-system.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JudgmentKind>))]
public enum JudgmentKind
{
    /// <summary>A specialty tag or promotion, decided by a Council vote.</summary>
    [JsonStringEnumMemberName("promotion")]
    Promotion,

    /// <summary>Instantiating a Soul, decided by a Council hiring vote.</summary>
    [JsonStringEnumMemberName("hiring")]
    Hiring,

    /// <summary>Retiring a Soul, decided by a Council vote.</summary>
    [JsonStringEnumMemberName("retirement")]
    Retirement,

    /// <summary>A charter or preferences change.</summary>
    [JsonStringEnumMemberName("charter")]
    Charter,
}

/// <summary>How sure the decider was. Recorded as written; it does not change the outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JudgmentConfidence>))]
public enum JudgmentConfidence
{
    /// <summary>Low confidence.</summary>
    [JsonStringEnumMemberName("low")]
    Low,

    /// <summary>Medium confidence.</summary>
    [JsonStringEnumMemberName("medium")]
    Medium,

    /// <summary>High confidence.</summary>
    [JsonStringEnumMemberName("high")]
    High,
}

/// <summary>One GC event a judgment relied on, with its provenance logged alongside it.</summary>
/// <param name="Seq">The GC sequence number of the input event.</param>
/// <param name="SourceTrust">The trust level the input carried when it was read.</param>
public sealed record JudgmentInput(long Seq, SourceTrust SourceTrust);

/// <summary>
/// A judgment about a Soul (docs/design/07-soul-system.md, "Auditable judgments"). It is posted to the GC as an immutable
/// message and written as a Vault decision file. <see cref="DecisionId"/> is the decision reference; the caller supplies it
/// so that a retry after a partial failure lands on the same record.
/// </summary>
/// <param name="DecisionId">The decision reference. Caller-generated and stable across retries.</param>
/// <param name="SoulId">The Soul the judgment is about.</param>
/// <param name="SoulVersion">The Soul version the judgment ran under (<see cref="SoulVersion.Version"/>), 1 or more.</param>
/// <param name="GcId">The GC the judgment is posted to.</param>
/// <param name="AgentId">The agent principal that made the judgment.</param>
/// <param name="Kind">What was decided.</param>
/// <param name="Title">A short name for the decision. Used for the file slug. Required.</param>
/// <param name="Inputs">The GC events relied on, with their provenance.</param>
/// <param name="Options">The options considered. Required, at least one.</param>
/// <param name="Chosen">The option chosen. Must be one of <paramref name="Options"/>.</param>
/// <param name="Reasoning">Why. Required.</param>
/// <param name="Confidence">How sure the decider was.</param>
/// <param name="DecidedAt">When the decision was made.</param>
public sealed record DecisionRecord(
    Guid DecisionId,
    Guid SoulId,
    int SoulVersion,
    Guid GcId,
    Guid AgentId,
    JudgmentKind Kind,
    string Title,
    IReadOnlyList<JudgmentInput> Inputs,
    IReadOnlyList<string> Options,
    string Chosen,
    string Reasoning,
    JudgmentConfidence Confidence,
    DateTimeOffset DecidedAt);
