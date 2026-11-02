namespace Bazaar.Contracts.Soul;

/// <summary>
/// One entry in a Soul's version chain (docs/design/07-soul-system.md, "Versioning"). Charter and preferences change only
/// by appending a version; nothing is overwritten. Messages and judgments record the <see cref="Version"/> they ran under.
/// </summary>
/// <param name="SoulId">The Soul this version belongs to.</param>
/// <param name="Version">1 for the first version, then +1 per append.</param>
/// <param name="ParentVersion">The version this one follows; null only for version 1.</param>
/// <param name="CharterSha256">Lowercase hex SHA-256 of the charter Vault file.</param>
/// <param name="PreferencesJson">Preferences as canonical JSON (sorted keys, no insignificant whitespace).</param>
/// <param name="ContentSha256">Lowercase hex SHA-256 over the charter hash and the canonical preferences.</param>
/// <param name="Author">Who appended the version.</param>
/// <param name="Reason">Why the version was appended. Never empty.</param>
/// <param name="DecisionRef">The decision record that approved the change, if any.</param>
/// <param name="CreatedAt">Set by the database when the row is inserted.</param>
public sealed record SoulVersion(
    Guid SoulId,
    int Version,
    int? ParentVersion,
    string CharterSha256,
    string PreferencesJson,
    string ContentSha256,
    string Author,
    string Reason,
    string? DecisionRef,
    DateTimeOffset CreatedAt);

/// <summary>What a writer asks to append. The service derives the version number, parent and hashes.</summary>
/// <param name="SoulId">The Soul to append to. A Soul with no versions yet is created by appending at expected version 0.</param>
/// <param name="ExpectedVersion">The head version the writer read. The append is refused unless it is still the head.</param>
/// <param name="CharterSha256">Lowercase hex SHA-256 of the charter Vault file being recorded.</param>
/// <param name="PreferencesJson">Preferences as JSON text; the service canonicalises it.</param>
/// <param name="Author">Who is appending.</param>
/// <param name="Reason">Why. Required.</param>
/// <param name="DecisionRef">Optional decision record id.</param>
public sealed record SoulVersionAppend(
    Guid SoulId,
    int ExpectedVersion,
    string CharterSha256,
    string PreferencesJson,
    string Author,
    string Reason,
    string? DecisionRef = null);
