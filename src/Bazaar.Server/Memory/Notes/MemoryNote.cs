using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Notes;

/// <summary>
/// Typed front matter of a memory note (docs/design/08-memory-system.md, section 1). The body is held by
/// <see cref="NoteDocument"/>. Trust fields are what the note claims; the server assigns the real tier
/// through <see cref="SoulGuard"/> and never trusts the claimed value.
/// </summary>
/// <param name="Id">Stable note id, usually a ULID.</param>
/// <param name="Type">Note type; must match <see cref="MemoryNoteTypes.TypeFor"/> for <paramref name="Kind"/>.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Project">Project slug for project notes, otherwise null.</param>
/// <param name="Kind">Memory kind.</param>
/// <param name="Status">Active or deprecated.</param>
/// <param name="Version">Monotonic version, at least 1; updates are optimistic on it.</param>
/// <param name="Trust">Promotion tier the note claims.</param>
/// <param name="SourceTrust">Provenance the note claims (doc 15 enum).</param>
/// <param name="Tainted">Whether the note was written from a tainted session.</param>
/// <param name="Origin">Where the note came from, or null.</param>
/// <param name="DerivedFrom">Note ids this note was built from.</param>
/// <param name="Subjects">Subject ids named in the note, for personal-data handling.</param>
/// <param name="Deprecated">Deprecation record, required when status is deprecated.</param>
/// <param name="Supersedes">Note ids this note replaces.</param>
public sealed record MemoryNote(
    string Id,
    string Type,
    string Title,
    string? Project,
    MemoryKind Kind,
    MemoryNoteStatus Status,
    long Version,
    MemoryTier Trust,
    ContentTrust SourceTrust,
    bool Tainted,
    MemoryOrigin? Origin,
    IReadOnlyList<string> DerivedFrom,
    IReadOnlyList<string> Subjects,
    MemoryDeprecation? Deprecated,
    IReadOnlyList<string> Supersedes);

/// <summary>Where a note came from: a GC and the messages it was written from.</summary>
/// <param name="Gc">GC id, or null.</param>
/// <param name="Messages">Message ids.</param>
public sealed record MemoryOrigin(string? Gc, IReadOnlyList<string> Messages);

/// <summary>Why and by whom a note was deprecated.</summary>
/// <param name="Reason">Free-text reason.</param>
/// <param name="By">Who deprecated it.</param>
/// <param name="At">When, as written by the server (ISO 8601).</param>
public sealed record MemoryDeprecation(string Reason, string By, string At);
