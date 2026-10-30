namespace Bazaar.Contracts.Memory;

/// <summary>
/// Input to <c>memory_store</c>: creates one note in the agent's private area or a shared area.
/// </summary>
/// <param name="AgentId">The agent making the write. Taken from the authenticated session by the server.</param>
/// <param name="Scope">One of <c>private</c>, <c>project</c>, <c>company</c> (docs/design/08-memory-system.md, section 1).</param>
/// <param name="Kind">One of <c>episodic</c>, <c>semantic</c>, <c>procedural</c>, <c>failure</c>, <c>preference</c>, <c>working</c>, <c>hive</c>.</param>
/// <param name="Title">Short note title.</param>
/// <param name="Body">Note body in Markdown. Size limits are enforced by the memory validator.</param>
/// <param name="Origin">Provenance label, such as <c>agent</c>, <c>external</c> or <c>owner</c> (doc 15 provenance enum).</param>
/// <param name="TurnId">The turn the write belongs to, when known.</param>
public sealed record MemoryStoreRequest(
    string AgentId,
    string Scope,
    string Kind,
    string Title,
    string Body,
    string Origin,
    string? TurnId);

/// <summary>Result of <c>memory_store</c>.</summary>
/// <param name="NoteId">Stable id of the new note.</param>
/// <param name="Path">Vault path of the note file.</param>
/// <param name="Version">Version of the note after the write; starts at 1.</param>
/// <param name="Tier">Promotion tier the note started at (T0 to T3 names from doc 08, section 5).</param>
/// <param name="Flagged">True when validation flagged the content, such as text claiming a role or approval.</param>
public sealed record MemoryStoreResult(
    string NoteId,
    string Path,
    long Version,
    string Tier,
    bool Flagged);
