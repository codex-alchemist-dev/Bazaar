namespace Bazaar.Server.Fun.Mirror;

/// <summary>
/// A raw observation of an owner decision (docs/design/26-fun-systems.md §5, <c>mirror_signal</c>).
/// </summary>
/// <param name="Id">Signal identifier.</param>
/// <param name="Kind">Signal category (e.g. "accept/reject", "owner edits").</param>
/// <param name="SourceRef">Reference to the origin GC message, card or review.</param>
/// <param name="Weight">Weighting of the observation.</param>
/// <param name="Payload">Observation details or reason text.</param>
/// <param name="CreatedAt">When the observation occurred.</param>
public sealed record MirrorSignal(
    Guid Id,
    string Kind,
    string SourceRef,
    double Weight,
    string Payload,
    DateTimeOffset CreatedAt);

/// <summary>
/// A learned owner preference rule (doc 26 §5). Applies only when <see cref="EvidenceCount"/> >= threshold.
/// </summary>
/// <param name="RuleId">Stable rule identifier.</param>
/// <param name="Topic">Topic or domain (e.g. "code-style", "task-prioritization").</param>
/// <param name="Instruction">Learned preference instruction.</param>
/// <param name="EvidenceCount">Number of supporting owner decisions.</param>
/// <param name="EvidenceRefs">References to observations supporting this rule.</param>
/// <param name="CreatedAt">When the rule was created.</param>
public sealed record MirrorRule(
    string RuleId,
    string Topic,
    string Instruction,
    int EvidenceCount,
    IReadOnlyList<string> EvidenceRefs,
    DateTimeOffset CreatedAt);

/// <summary>
/// The versioned preference profile (docs/design/26-fun-systems.md §5, <c>mirror_profile</c>).
/// </summary>
/// <param name="Version">1-based profile version.</param>
/// <param name="Rules">Active rules meeting the evidence threshold.</param>
/// <param name="CreatedAt">When the profile version was generated.</param>
public sealed record MirrorProfile(
    int Version,
    IReadOnlyList<MirrorRule> Rules,
    DateTimeOffset CreatedAt);

/// <summary>
/// Annotation and optional rewrite offer for an agent suggestion in the owner's view (doc 26 §5).
/// Invariant: <see cref="OriginalText"/> is ALWAYS retained and visible; Mirror never removes an item.
/// </summary>
/// <param name="SuggestionId">Identifier of the agent suggestion.</param>
/// <param name="OriginalText">The unmodified original text from the agent.</param>
/// <param name="AppliedRuleIds">Rules that matched this suggestion.</param>
/// <param name="OfferedRewrite">Optional alternative rewrite based on owner preferences.</param>
/// <param name="RankingScore">Computed ranking score for ordering suggestions.</param>
public sealed record MirrorAnnotation(
    string SuggestionId,
    string OriginalText,
    IReadOnlyList<string> AppliedRuleIds,
    string? OfferedRewrite,
    double RankingScore);
