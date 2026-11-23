using System.Text;

namespace Bazaar.Server.RepoMap;

/// <summary>
/// Builds a token-bounded map of declarations in a worktree. Focus files come first, then files ranked by symbol density,
/// shallower paths first, then path order. Output is deterministic and never exceeds the token budget.
/// Files under bin, obj and .git, binary files and files that are not valid UTF-8 are skipped.
/// </summary>
public sealed class RepoMapBuilder : IRepoMapBuilder
{
    /// <summary>Largest accepted token budget. Packing counts the whole map on each line, so the cap bounds the cost.</summary>
    public const int MaxTokenBudget = 16_000;

    /// <summary>Largest file read for symbol extraction, in bytes. Bigger files are skipped.</summary>
    public const int MaxFileBytes = 512 * 1024;

    private const int _maxSignatureLength = 200;
    private const int _noFocus = int.MaxValue;
    private const int _maxListingBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan _listingTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IRepoWorkspace _workspace;
    private readonly IReadOnlyList<ISymbolExtractor> _extractors;
    private readonly ITokenCounter _tokens;

    /// <summary>Creates the builder.</summary>
    public RepoMapBuilder(IRepoWorkspace workspace, IEnumerable<ISymbolExtractor> extractors, ITokenCounter tokens)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(tokens);
        _workspace = workspace;
        _extractors = [.. extractors];
        _tokens = tokens;
    }

    /// <inheritdoc />
    public async Task<RepoMapResult> BuildAsync(RepoMapRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RepoMapRules.IsSafeRoot(request.Root))
        {
            throw new ArgumentException("Root must be \".\" or a relative path inside the worktree.", nameof(request));
        }

        if (request.TokenBudget < 1 || request.TokenBudget > MaxTokenBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"TokenBudget must be from 1 to {MaxTokenBudget}.");
        }

        var focus = ValidateFocus(request.FocusFiles);
        var listed = await ListFilesAsync(RepoMapRules.Normalize(request.Root), ct);

        var candidates = new List<Candidate>();
        foreach (var path in listed)
        {
            ct.ThrowIfCancellationRequested();
            var extractor = _extractors.FirstOrDefault(e => e.Supports(path));
            if (extractor is null)
            {
                continue;
            }

            var bytes = await _workspace.ReadFileAsync(path, MaxFileBytes, ct);
            if (bytes is null || bytes.AsSpan().Contains((byte)0))
            {
                continue;
            }

            string content;
            try
            {
                content = _strictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            var symbols = await extractor.ExtractAsync(path, content, ct);
            if (symbols.Count == 0)
            {
                continue;
            }

            var rank = focus.TryGetValue(path, out var index) ? index : _noFocus;
            candidates.Add(new Candidate(path, symbols.OrderBy(s => s.Line).ToList(), LineCount(content), rank));
        }

        candidates.Sort(Compare);
        return Pack(candidates, request.TokenBudget);
    }

    /// <summary>Packs candidates in rank order. A file header and each symbol line are kept only while the whole map stays within the budget.</summary>
    private RepoMapResult Pack(IReadOnlyList<Candidate> ordered, int budget)
    {
        var sb = new StringBuilder();
        var includedFiles = 0;
        var omittedFiles = 0;
        var includedSymbols = 0;
        var omittedSymbols = 0;

        foreach (var candidate in ordered)
        {
            var mark = sb.Length;
            sb.Append(candidate.Path).Append('\n');
            if (_tokens.Count(sb.ToString()) > budget)
            {
                sb.Length = mark;
                omittedFiles++;
                omittedSymbols += candidate.Symbols.Count;
                continue;
            }

            var shown = 0;
            foreach (var symbol in candidate.Symbols)
            {
                var symbolMark = sb.Length;
                sb.Append("  ").Append(symbol.Line).Append(": ").Append(RepoMapRules.SingleLine(symbol.Signature, _maxSignatureLength)).Append('\n');
                if (_tokens.Count(sb.ToString()) > budget)
                {
                    sb.Length = symbolMark;
                    omittedSymbols++;
                    continue;
                }

                shown++;
                includedSymbols++;
            }

            if (shown == 0)
            {
                // A header with no symbol under it is noise, so the file is dropped whole.
                sb.Length = mark;
                omittedFiles++;
                continue;
            }

            includedFiles++;
        }

        var text = sb.ToString();
        return new RepoMapResult(text, includedFiles, omittedFiles, includedSymbols, omittedSymbols, _tokens.Count(text));
    }

    /// <summary>Lists source files under the root, excluding bin, obj and .git, in ordinal path order.</summary>
    private async Task<IReadOnlyList<string>> ListFilesAsync(string root, CancellationToken ct)
    {
        var argv = new[] { "rg", "--no-config", "--files", "--null", "--glob=!**/.git/**", "--glob=!**/bin/**", "--glob=!**/obj/**", "--", root };
        var output = await _workspace.RunAsync(argv, _listingTimeout, _maxListingBytes, ct);
        if (output.TimedOut)
        {
            throw new RepoToolException("File listing timed out.");
        }

        if (output.ExitCode != 0)
        {
            throw new RepoToolException($"File listing failed with exit code {output.ExitCode}.");
        }

        var parts = output.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        // When the cap cut the output, the last entry may be a partial path. Drop it.
        var count = output.OutputTruncated ? Math.Max(0, parts.Length - 1) : parts.Length;
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var path = RepoMapRules.Normalize(parts[i]);
            if (RepoMapRules.IsSafeRelative(path) && !RepoMapRules.IsExcluded(path))
            {
                paths.Add(path);
            }
        }

        return [.. paths];
    }

    /// <summary>Checks focus paths and maps each to its position in the request. Duplicates keep the first position.</summary>
    private static Dictionary<string, int> ValidateFocus(IReadOnlyList<string>? focusFiles)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        if (focusFiles is null)
        {
            return rank;
        }

        foreach (var raw in focusFiles)
        {
            var path = RepoMapRules.Normalize(raw);
            if (!RepoMapRules.IsSafeRelative(path))
            {
                throw new ArgumentException($"Focus file '{raw}' is not a relative path inside the worktree.", nameof(focusFiles));
            }

            rank.TryAdd(path, rank.Count);
        }

        return rank;
    }

    /// <summary>Focus files first in request order; then density (symbols per line, compared without division), depth, and path.</summary>
    private static int Compare(Candidate x, Candidate y)
    {
        var xFocused = x.FocusRank != _noFocus;
        var yFocused = y.FocusRank != _noFocus;
        if (xFocused || yFocused)
        {
            if (xFocused && yFocused)
            {
                return x.FocusRank.CompareTo(y.FocusRank);
            }

            return xFocused ? -1 : 1;
        }

        var xScaled = (long)x.Symbols.Count * y.LineCount;
        var yScaled = (long)y.Symbols.Count * x.LineCount;
        var byDensity = yScaled.CompareTo(xScaled);
        if (byDensity != 0)
        {
            return byDensity;
        }

        var byDepth = Depth(x.Path).CompareTo(Depth(y.Path));
        return byDepth != 0 ? byDepth : string.CompareOrdinal(x.Path, y.Path);
    }

    private static int Depth(string path) => path.Count(c => c == '/');

    private static int LineCount(string content) => content.Count(c => c == '\n') + 1;

    private sealed record Candidate(string Path, IReadOnlyList<CodeSymbol> Symbols, int LineCount, int FocusRank);
}
