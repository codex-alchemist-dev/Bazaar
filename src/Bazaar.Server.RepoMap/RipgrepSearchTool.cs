namespace Bazaar.Server.RepoMap;

/// <summary>
/// Runs ripgrep through <see cref="IRepoWorkspace"/>. The pattern and glob each reach ripgrep as one argument, never through a shell.
/// Output is parsed with NUL path separators and treated as data. Paths under bin, obj and .git are dropped.
/// </summary>
public sealed class RipgrepSearchTool : ISearchTool
{
    /// <summary>Largest accepted <see cref="SearchRequest.MaxResults"/>.</summary>
    public const int MaxResultsLimit = 1000;

    /// <summary>Longest accepted pattern, in characters.</summary>
    public const int MaxPatternLength = 256;

    /// <summary>Longest accepted glob, in characters.</summary>
    public const int MaxGlobLength = 256;

    /// <summary>Longest match text returned, in characters.</summary>
    public const int MaxLineLength = 500;

    /// <summary>Output cap for one search, in bytes.</summary>
    public const int MaxOutputBytes = 1024 * 1024;

    private static readonly TimeSpan _searchTimeout = TimeSpan.FromSeconds(30);

    private readonly IRepoWorkspace _workspace;

    /// <summary>Creates the tool over a worktree.</summary>
    public RipgrepSearchTool(IRepoWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePattern(request.Pattern);
        if (request.MaxResults < 1 || request.MaxResults > MaxResultsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"MaxResults must be from 1 to {MaxResultsLimit}.");
        }

        var argv = new List<string> { "rg", "--no-config", "--line-number", "--no-heading", "--with-filename", "--null", "--color=never", "--max-columns=500", "--max-columns-preview" };
        if (request.Glob is not null)
        {
            ValidateGlob(request.Glob);
            argv.Add("--glob=" + request.Glob);
        }

        // User globs come before the exclusions so a later exclusion wins over a user glob that would re-include bin, obj or .git.
        argv.Add("--glob=!**/.git/**");
        argv.Add("--glob=!**/bin/**");
        argv.Add("--glob=!**/obj/**");
        if (request.CaseInsensitive)
        {
            argv.Add("--ignore-case");
        }

        argv.Add("--regexp=" + request.Pattern);
        argv.Add("--");
        argv.Add(".");

        var output = await _workspace.RunAsync(argv, _searchTimeout, MaxOutputBytes, ct);
        if (output.TimedOut)
        {
            throw new RepoToolException("Search timed out.");
        }

        if (output.ExitCode is not (0 or 1))
        {
            throw new RepoToolException($"Search failed with exit code {output.ExitCode}.");
        }

        var truncated = output.OutputTruncated;
        var text = output.Stdout;
        if (truncated)
        {
            // The cap can cut a line in half. Keep only complete lines.
            var lastNewline = text.LastIndexOf('\n');
            text = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        }

        var matches = new List<SearchMatch>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Parse(line.TrimEnd('\r'));
            if (match is null)
            {
                continue;
            }

            if (matches.Count == request.MaxResults)
            {
                truncated = true;
                break;
            }

            matches.Add(match);
        }

        return new SearchResult(matches, truncated);
    }

    /// <summary>Parses one "path NUL line:text" record. Returns null for anything malformed, unsafe or excluded.</summary>
    private static SearchMatch? Parse(string record)
    {
        var nul = record.IndexOf('\0');
        if (nul <= 0)
        {
            return null;
        }

        var path = RepoMapRules.Normalize(record[..nul]);
        var rest = record[(nul + 1)..];
        var colon = rest.IndexOf(':');
        if (colon <= 0 || !int.TryParse(rest.AsSpan(0, colon), out var lineNumber) || lineNumber < 1)
        {
            return null;
        }

        if (!RepoMapRules.IsSafeRelative(path) || RepoMapRules.IsExcluded(path))
        {
            return null;
        }

        return new SearchMatch(path, lineNumber, RepoMapRules.Printable(rest[(colon + 1)..], MaxLineLength));
    }

    private static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength)
        {
            throw new ArgumentException($"Pattern must be 1 to {MaxPatternLength} characters.", nameof(pattern));
        }

        if (pattern.Any(c => c is '\0' or '\n' or '\r'))
        {
            throw new ArgumentException("Pattern must not contain line breaks or NUL.", nameof(pattern));
        }
    }

    private static void ValidateGlob(string glob)
    {
        if (glob.Length == 0 || glob.Length > MaxGlobLength)
        {
            throw new ArgumentException($"Glob must be 1 to {MaxGlobLength} characters.", nameof(glob));
        }

        if (glob[0] is '/' or '!' || glob.Contains('\0') || glob.Contains('\n') || glob.Contains('\r'))
        {
            throw new ArgumentException("Glob must be relative, must not start with '!', and must not contain line breaks or NUL.", nameof(glob));
        }

        foreach (var segment in glob.Split('/'))
        {
            if (segment == "..")
            {
                throw new ArgumentException("Glob must not contain a '..' segment.", nameof(glob));
            }
        }
    }
}
