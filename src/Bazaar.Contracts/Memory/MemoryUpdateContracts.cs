namespace Bazaar.Contracts.Memory;

/// <summary>One front matter field change in a <c>memory_update</c> patch.</summary>
/// <param name="Field">Front matter key, such as <c>status</c> or <c>tags</c>.</param>
/// <param name="Value">New value as text.</param>
public sealed record MemoryFieldChange(string Field, string Value);

/// <summary>
/// Input to <c>memory_update</c>. The write is refused when <see cref="ExpectedVersion"/> is stale.
/// At least one of <see cref="Body"/> or <see cref="FieldChanges"/> is expected to be set.
/// </summary>
/// <param name="AgentId">The agent making the change.</param>
/// <param name="NoteId">The note to change.</param>
/// <param name="ExpectedVersion">The version the caller last read.</param>
/// <param name="Body">Replacement body, or null to keep the body.</param>
/// <param name="FieldChanges">Front matter changes, or null to keep the front matter.</param>
public sealed record MemoryUpdateRequest(
    string AgentId,
    string NoteId,
    long ExpectedVersion,
    string? Body,
    IReadOnlyList<MemoryFieldChange>? FieldChanges);

/// <summary>Result of <c>memory_update</c>.</summary>
/// <param name="NoteId">The note the call targeted.</param>
/// <param name="Updated">True when the change was written.</param>
/// <param name="Version">The note version after the call. Equals the current version when <see cref="Updated"/> is false.</param>
/// <param name="Conflict">True when the write was refused because <c>ExpectedVersion</c> was stale.</param>
public sealed record MemoryUpdateResult(
    string NoteId,
    bool Updated,
    long Version,
    bool Conflict);
