namespace Bazaar.Server.RepoMap;

/// <summary>One matching line.</summary>
/// <param name="Path">Worktree-relative path with forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Text">The line, with control characters replaced and length capped. Data only.</param>
public sealed record SearchMatch(string Path, int Line, string Text);

/// <summary>The matches of one search.</summary>
/// <param name="Matches">Matches in ripgrep order.</param>
/// <param name="Truncated">True when more matches existed than <see cref="SearchRequest.MaxResults"/> or the output cap cut the list short.</param>
public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool Truncated);
