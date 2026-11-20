namespace Bazaar.Kodo.Runtime;

/// <summary>One file listed by a runtime manifest: its slot-relative path, SHA-256 (lowercase hex) and size in bytes.</summary>
public sealed record RuntimeFileEntry(string Path, string Sha256, long Size);

/// <summary>
/// The manifest of one runtime slot (doc 46 section 6.1). The files are sorted by path, unique, and cover every
/// file the slot may contain. The canonical JSON of this record is what the release key signs.
/// </summary>
public sealed record RuntimeManifest(string Version, string SlotId, string BuildId, IReadOnlyList<RuntimeFileEntry> Files);

/// <summary>
/// A manifest with its detached signature. <see cref="KeyId"/> names the trusted key, and <see cref="Signature"/> is the
/// base64 signature over the canonical JSON of <see cref="Manifest"/>. Either may be null, and then nothing verifies.
/// </summary>
public sealed record SignedRuntimeManifest(RuntimeManifest Manifest, string? KeyId, string? Signature);
