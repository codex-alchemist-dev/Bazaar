namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// The file seam for the sentinel (doc 46 sections 6.2 and 6.3). The real store is OS-specific; the sentinel logic only sees
/// these calls. Reads return null for a missing file and throw <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> for a file that exists but cannot be read.
/// </summary>
public interface ISentinelFileStore
{
    /// <summary>The bytes of a file in the runtime root, or null when it does not exist.</summary>
    byte[]? ReadRootFile(string name);

    /// <summary>
    /// Every file of a slot by slot-relative path ('/' separators, ordinal), including the manifest envelope. Empty when the
    /// slot has no files.
    /// </summary>
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> ReadSlot(string slotId);

    /// <summary>
    /// Replaces a root file atomically: the implementation writes a temp file in the same directory, flushes it, and replaces the
    /// target in one step (doc 46 6.3).
    /// </summary>
    void ReplaceRootFile(string name, ReadOnlyMemory<byte> bytes);

    /// <summary>Deletes a root file when it exists. Used for a stray temp file and the probation marker.</summary>
    void DeleteRootFile(string name);
}
