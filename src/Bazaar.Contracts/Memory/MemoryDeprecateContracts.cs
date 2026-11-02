namespace Bazaar.Contracts.Memory;

/// <summary>
/// Input to <c>memory_deprecate</c>. There is no hard-delete tool: the note file stays and drops out of retrieval
/// (docs/design/08-memory-system.md, section 2).
/// </summary>
/// <param name="AgentId">The agent making the change.</param>
/// <param name="NoteId">The note to deprecate.</param>
/// <param name="Reason">Why the note is deprecated. Never empty; recorded in front matter.</param>
public sealed record MemoryDeprecateRequest(string AgentId, string NoteId, string Reason);

/// <summary>Result of <c>memory_deprecate</c>.</summary>
/// <param name="NoteId">The note the call targeted.</param>
/// <param name="Version">The note version after the deprecation.</param>
/// <param name="Deprecated">True when the note is now deprecated (also true when it already was).</param>
public sealed record MemoryDeprecateResult(string NoteId, long Version, bool Deprecated);
