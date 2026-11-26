namespace Bazaar.Server.RepoMap;

/// <summary>
/// Searches the files of one worktree for a pattern. This is the Read capability of the coding harness.
/// </summary>
public interface ISearchTool
{
    /// <summary>The capability class a caller needs to use this tool.</summary>
    const string CapabilityClass = "Read";

    /// <summary>
    /// Returns the matching lines. Matched text is repository content and is untrusted data; callers must never execute it.
    /// An invalid request throws <see cref="ArgumentException"/>. A timeout or a failed search throws <see cref="RepoToolException"/>,
    /// so a partial result is never reported as complete.
    /// </summary>
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct);
}
