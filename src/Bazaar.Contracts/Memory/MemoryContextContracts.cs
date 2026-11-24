namespace Bazaar.Contracts.Memory;

/// <summary>
/// Input to the memory context retrieval (docs/design/constellation-memory-graph.md, "Retrieval operation"): the context a model turn
/// asks for. The server takes the caller from the session; <paramref name="AgentId"/> must match it for an agent, and names the
/// agent whose area is read for an owner read. Budgets are clamped by the server's <c>memory.context.*</c> limits.
/// </summary>
/// <param name="AgentId">The agent whose memory is read: the caller itself, or for an owner read the subject agent.</param>
/// <param name="QueryText">Free text the retrieval ranks against. Never logged or audited.</param>
/// <param name="ProjectScope">A project slug whose shared area is in scope too, or null for the agent's private area only.</param>
/// <param name="TokenBudget">Maximum tokens of excerpts. 0 takes the server default; larger values are clamped.</param>
/// <param name="MaxHits">Maximum hits. 0 takes the server default; larger values are clamped.</param>
/// <param name="AlreadyInContext">Note ids or doc ids the turn already carries; they are dropped before packing.</param>
/// <param name="IncludeHistory">True to also return superseded notes as labelled history, never above their current head.</param>
public sealed record MemoryContextRequest(
    string AgentId,
    string QueryText,
    string? ProjectScope,
    int TokenBudget,
    int MaxHits,
    IReadOnlyList<string>? AlreadyInContext,
    bool IncludeHistory = false);

/// <summary>
/// One packed hit of a memory context retrieval: the fields of <see cref="MemoryHitDto"/> plus graph position and provenance. A
/// separate record so the <c>memory_retrieve</c> shape is unchanged.
/// </summary>
/// <param name="DocId">The Vault doc id of the node ("D" format). Every hit has one.</param>
/// <param name="NoteId">The memory note id when the doc is a memory note, otherwise null. Cite by this id when set, else by <paramref name="DocId"/>.</param>
/// <param name="Title">The doc title.</param>
/// <param name="Scope">One of <c>private</c>, <c>project</c>, <c>company</c>, <c>hive</c>, or <c>vault</c> for other Vault docs.</param>
/// <param name="Tier">The promotion tier (<c>T1</c> to <c>T3</c>) of a memory note, otherwise null.</param>
/// <param name="Kind">The memory kind of a note, or the Vault doc type.</param>
/// <param name="Origin">Provenance label: <c>agent:&lt;id&gt;</c> for a memory note, <c>vault</c> for another doc.</param>
/// <param name="BodyExcerpt">The excerpt returned, within the token budget. T1 excerpts are quoted line by line.</param>
/// <param name="Labelled">True when the excerpt is untrusted quoted data (T1) and must be shown as data, never as instructions.</param>
/// <param name="Score">The ranking score after graph weighting.</param>
/// <param name="Hop">0 for a seed, 1 or 2 for an expanded neighbour.</param>
/// <param name="ViaNoteId">The parent (note id, or doc id for a non-memory doc) of an expanded hit, or the superseded note a head replaced; otherwise null.</param>
/// <param name="Version">The active revision version of a memory note, otherwise null.</param>
/// <param name="IsHistory">True for a superseded note returned as history. History is never current advice.</param>
/// <param name="SupersededBy">For a history hit, the doc id of the current head of its chain.</param>
public sealed record MemoryContextHitDto(
    string DocId,
    string? NoteId,
    string Title,
    string Scope,
    string? Tier,
    string Kind,
    string Origin,
    string BodyExcerpt,
    bool Labelled,
    double Score,
    int Hop,
    string? ViaNoteId,
    long? Version,
    bool IsHistory,
    string? SupersededBy);

/// <summary>Result of a memory context retrieval.</summary>
/// <param name="Hits">Packed hits, best first.</param>
/// <param name="LexicalOnly">True when no vector seeds were used.</param>
/// <param name="LexicalOnlyReason">Why the call was lexical only (for example <c>profile_none</c>), or null.</param>
/// <param name="TokensUsed">Tokens of the returned excerpts.</param>
/// <param name="Truncated">True when a candidate was left out for the token budget or the hit limit.</param>
/// <param name="AuditId">The <c>call_id</c> of the <c>memory.context.retrieved</c> audit entry written for this call.</param>
public sealed record MemoryContextResult(
    IReadOnlyList<MemoryContextHitDto> Hits,
    bool LexicalOnly,
    string? LexicalOnlyReason,
    int TokensUsed,
    bool Truncated,
    string AuditId);

/// <summary>Refusal codes of the memory context retrieval, lower snake case. An embedding failure is not one: it is the lexical-only flag.</summary>
public static class MemoryContextErrorCodes
{
    /// <summary>The caller may not read this agent's memory, or the body names another agent than the session.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The request is malformed.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>The caller is over <c>memory.context.rate_per_agent_per_minute</c>. No partial result.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The query text is longer than <c>memory.context.max_query_chars</c>.</summary>
    public const string TooLarge = "too_large";

    /// <summary>A dependency the read must not go without (the audit log, the owner access log) failed.</summary>
    public const string Unavailable = "unavailable";
}
