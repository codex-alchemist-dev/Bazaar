namespace Bazaar.Server.CodingEdit;

/// <summary>Why an edit was refused. Every refusal is data for the model, never an exception.</summary>
public enum EditRejectReason
{
    /// <summary>The path leaves the worktree: <c>..</c>, an absolute path, a drive or device marker, NUL, or a symlink that leaves it.</summary>
    PathEscape,

    /// <summary>The file does not exist, so a line-anchored or search/replace edit has nothing to change.</summary>
    NotFound,

    /// <summary>The lines at the anchored range no longer hash to the value the model proved.</summary>
    AnchorMismatch,

    /// <summary>The search text occurs more than once.</summary>
    AmbiguousMatch,

    /// <summary>The search text does not occur.</summary>
    NoMatch,

    /// <summary>The edit is not well formed: wrong format for this agent, empty search, bad range, bad hash, or a missing value.</summary>
    Malformed,

    /// <summary>The file is not valid UTF-8, so a text edit would change its bytes.</summary>
    NotText,

    /// <summary>The file changed between the read and the write. Nothing was written.</summary>
    Conflict,

    /// <summary>The write did not verify. The original bytes were restored.</summary>
    WriteFailed,
}

/// <summary>The outcome of one edit.</summary>
/// <param name="Applied">True when the file now holds the edited content, including a no-op that already held it.</param>
/// <param name="Reason">The refusal reason when <paramref name="Applied"/> is false; null otherwise.</param>
/// <param name="Path">The normalized worktree-relative path.</param>
/// <param name="BeforeSha256">SHA-256 (hex) of the file before the edit, or null when it did not exist.</param>
/// <param name="AfterSha256">SHA-256 (hex) of the file after the edit, or null when refused.</param>
/// <param name="ChangedLines">Lines removed or added by the edit. Zero for a no-op.</param>
/// <param name="Detail">A short human-readable note for refusals. It never contains file content.</param>
public sealed record EditResult(
    bool Applied,
    EditRejectReason? Reason,
    string Path,
    string? BeforeSha256,
    string? AfterSha256,
    int ChangedLines,
    string? Detail)
{
    /// <summary>A successful edit (or a no-op that already held the content).</summary>
    public static EditResult Succeeded(string path, string? beforeSha256, string afterSha256, int changedLines) =>
        new(true, null, path, beforeSha256, afterSha256, changedLines, null);

    /// <summary>A refused edit. Nothing was written.</summary>
    public static EditResult Refused(EditRejectReason reason, string path, string? detail = null) =>
        new(false, reason, path, null, null, 0, detail);
}
