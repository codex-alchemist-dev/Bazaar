namespace Bazaar.Server.RepoMap;

/// <summary>A search over the worktree.</summary>
/// <param name="Pattern">A regular expression in ripgrep syntax. It reaches the process as one argument, never through a shell. Must be 1 to 256 characters with no line breaks or NUL.</param>
/// <param name="Glob">Optional file filter such as "*.cs". Relative, with no ".." segment and no leading "!". Null searches every file.</param>
/// <param name="MaxResults">Most matches returned, from 1 to 1000.</param>
/// <param name="CaseInsensitive">Ignores letter case when true.</param>
public sealed record SearchRequest(string Pattern, string? Glob = null, int MaxResults = 100, bool CaseInsensitive = false);
