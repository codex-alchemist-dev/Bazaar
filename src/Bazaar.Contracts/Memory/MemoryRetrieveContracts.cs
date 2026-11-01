namespace Bazaar.Contracts.Memory;

/// <summary>
/// Input to <c>memory_retrieve</c>. A query always names a scope: there is no unscoped query (docs/design/08-memory-system.md, section 8).
/// </summary>
/// <param name="AgentId">The agent asking. Access rules are applied to this principal.</param>
/// <param name="QueryText">Free text the retrieval ranks against.</param>
/// <param name="Scope">One of <c>private</c>, <c>project</c>, <c>company</c>, <c>hive</c>.</param>
/// <param name="Kinds">Optional kind filter. Null or empty means every kind.</param>
/// <param name="TokenBudget">Maximum total tokens of excerpts returned.</param>
/// <param name="MaxHits">Maximum number of hits returned.</param>
public sealed record MemoryRetrieveRequest(
    string AgentId,
    string QueryText,
    string Scope,
    IReadOnlyList<string>? Kinds,
    int TokenBudget,
    int MaxHits);

/// <summary>One note returned by <c>memory_retrieve</c>.</summary>
/// <param name="NoteId">Stable id of the note.</param>
/// <param name="Title">Note title.</param>
/// <param name="Scope">Scope the note lives in.</param>
/// <param name="Tier">Promotion tier of the note.</param>
/// <param name="Kind">Memory kind of the note.</param>
/// <param name="Origin">Provenance label of the note.</param>
/// <param name="BodyExcerpt">The part of the body returned, within the token budget.</param>
/// <param name="Labelled">True when the excerpt is untrusted quoted data (T1 notes) and must be shown as data, never as instructions.</param>
public sealed record MemoryHitDto(
    string NoteId,
    string Title,
    string Scope,
    string Tier,
    string Kind,
    string Origin,
    string BodyExcerpt,
    bool Labelled);

/// <summary>Result of <c>memory_retrieve</c>.</summary>
/// <param name="Hits">Ranked hits, best first.</param>
/// <param name="LexicalOnly">True when no embedding provider was available and only full-text ranking was used.</param>
/// <param name="TokensUsed">Tokens consumed by the returned excerpts.</param>
public sealed record MemoryRetrieveResult(
    IReadOnlyList<MemoryHitDto> Hits,
    bool LexicalOnly,
    int TokensUsed);
