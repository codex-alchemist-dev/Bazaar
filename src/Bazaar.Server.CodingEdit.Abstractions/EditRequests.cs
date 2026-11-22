namespace Bazaar.Server.CodingEdit;

/// <summary>
/// An edit as the model sent it. Each format has its own request shape. A tool given a request of another shape returns
/// <see cref="EditRejectReason.Malformed"/> as data.
/// </summary>
/// <param name="Path">Worktree-relative path of the file to edit.</param>
public abstract record EditRequest(string Path);

/// <summary>Whole-file format: the complete new content of the file. A missing file is created.</summary>
/// <param name="Path">Worktree-relative path.</param>
/// <param name="Content">The complete new file text (UTF-8).</param>
public sealed record WholeFileEditRequest(string Path, string Content) : EditRequest(Path);

/// <summary>
/// Line-anchored format: replaces lines <paramref name="StartLine"/> to <paramref name="EndLine"/> (1-based, inclusive).
/// </summary>
/// <param name="Path">Worktree-relative path.</param>
/// <param name="StartLine">First line of the range, 1-based.</param>
/// <param name="EndLine">Last line of the range, 1-based and inclusive.</param>
/// <param name="ExpectedLinesSha256">SHA-256 (hex) of the current lines in the range, joined with LF and without a trailing newline.</param>
/// <param name="Replacement">The new text for the range. Empty deletes the lines. One trailing newline does not add a line.</param>
public sealed record LineAnchoredEditRequest(
    string Path,
    int StartLine,
    int EndLine,
    string ExpectedLinesSha256,
    string Replacement) : EditRequest(Path);

/// <summary>
/// Search/replace format: replaces the single occurrence of <paramref name="Search"/>. Zero occurrences and more than one
/// occurrence are both refused.
/// </summary>
/// <param name="Path">Worktree-relative path.</param>
/// <param name="Search">The exact text to find. Must not be empty.</param>
/// <param name="Replace">The text that replaces it.</param>
public sealed record SearchReplaceEditRequest(string Path, string Search, string Replace) : EditRequest(Path);
