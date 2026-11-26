namespace Bazaar.Kodo.Testing;

/// <summary>Cause strings of the scheduled self-checks (doc 46 section 17.2, PROPOSED names).</summary>
public static class SelfCheckCauses
{
    /// <summary>A file's hash differs from the recorded one, or a recorded file is gone. A data integrity finding, never repaired silently.</summary>
    public const string IntegrityMismatch = "integrity_mismatch";

    /// <summary>The scan found files that have no recorded hash.</summary>
    public const string UnrecordedFiles = "unrecorded_files";

    /// <summary>No recorded hashes are available, so nothing can be compared. Unknown, never green.</summary>
    public const string ManifestMissing = "manifest_missing";

    /// <summary>A file or directory could not be read. Unknown, never green.</summary>
    public const string ScanUnreadable = "scan_unreadable";
}
