namespace Bazaar.Server.CodingReview;

/// <summary>
/// Builds the reviewer's context (doc 17, Review flow and subagents). It is pure and deterministic: files are ordered by path, and
/// the caps are applied in a fixed order. The highlights (test-file changes and fake-success findings) come from the full change set
/// before any cap, so they always appear at the top. Every cut sets <see cref="ReviewContext.Truncated"/>.
/// </summary>
public sealed class ReviewContextBuilder(ReviewContextLimits limits)
{
    private readonly ReviewContextLimits _limits = limits ?? throw new ArgumentNullException(nameof(limits));

    /// <summary>Builds the context for one PR from the inputs the sources returned.</summary>
    public ReviewContext Build(
        ReviewPullRequestRef pullRequest,
        IReadOnlyList<DiffFile> files,
        TaskCardSummary? taskCard,
        GcThreadExcerpt? gcThread,
        ReviewCheckSummary? checks)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(pullRequest.BaseCommit) || string.IsNullOrWhiteSpace(pullRequest.HeadCommit))
        {
            throw new ArgumentException("A review needs both the base and the head commit.", nameof(pullRequest));
        }

        var ordered = files.OrderBy(f => f.Path.Text, StringComparer.Ordinal).ThenBy(f => f.Kind).ToList();
        var highlights = BuildHighlights(ordered, checks);

        var truncated = false;
        var kept = CapDiff(ordered, ref truncated, out var omittedFiles);

        var cappedTask = taskCard is null ? null : new TaskCardSummary(
            CapText(taskCard.Title, _limits.MaxTaskCardCharacters, ref truncated),
            CapText(taskCard.Summary, _limits.MaxTaskCardCharacters, ref truncated));
        var cappedGc = gcThread is null ? null : new GcThreadExcerpt(CapGcThread(gcThread.Lines, ref truncated));

        return new ReviewContext(pullRequest, highlights, kept, cappedTask, cappedGc, checks, truncated || omittedFiles > 0, omittedFiles);
    }

    /// <summary>True for a path under a <c>tests</c> folder, or a file named <c>*Tests.cs</c> or <c>*Test.cs</c>. Backslashes count as separators.</summary>
    internal static bool IsTestPath(string path)
    {
        var normalised = path.Replace('\\', '/');
        var name = normalised[(normalised.LastIndexOf('/') + 1)..];
        var inTestsFolder = normalised.Split('/').SkipLast(1).Any(s => s.Equals("tests", StringComparison.Ordinal));
        return inTestsFolder || name.EndsWith("Tests.cs", StringComparison.Ordinal) || name.EndsWith("Test.cs", StringComparison.Ordinal);
    }

    private static List<ReviewCheckFlag> BuildHighlights(IReadOnlyList<DiffFile> ordered, ReviewCheckSummary? checks)
    {
        var highlights = new List<ReviewCheckFlag>();
        var seen = new HashSet<(string Code, string? Path)>();

        foreach (var file in ordered.Where(f => IsTestPath(f.Path.Text)))
        {
            if (seen.Add((ReviewCheckFlagCodes.TestFileChanged, file.Path.Text)))
            {
                highlights.Add(new ReviewCheckFlag(
                    ReviewCheckFlagCodes.TestFileChanged,
                    file.Path.Text,
                    new UntrustedText(UntrustedTextSource.Path, $"{file.Kind} test file: {file.Path.Text}")));
            }
        }

        if (checks is not null)
        {
            foreach (var flag in checks.Flags.Where(f => IsHighlightCode(f.Code)))
            {
                if (seen.Add((flag.Code, flag.Path)))
                {
                    highlights.Add(flag);
                }
            }
        }

        return highlights;
    }

    private static bool IsHighlightCode(string code) =>
        code.Equals(ReviewCheckFlagCodes.TestFileChanged, StringComparison.Ordinal) || ReviewCheckFlagCodes.FakeSuccess.Contains(code);

    private List<DiffFile> CapDiff(IReadOnlyList<DiffFile> ordered, ref bool truncated, out int omittedFiles)
    {
        var kept = new List<DiffFile>();
        var remaining = _limits.MaxDiffCharacters;
        omittedFiles = 0;

        foreach (var file in ordered)
        {
            if (remaining <= 0)
            {
                omittedFiles++;
                continue;
            }

            var hunks = new List<DiffHunk>();
            foreach (var hunk in file.Hunks)
            {
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                if (hunk.Body.Length <= remaining)
                {
                    hunks.Add(hunk);
                    remaining -= hunk.Body.Length;
                }
                else
                {
                    hunks.Add(hunk with { Body = hunk.Body with { Text = hunk.Body.Text[..remaining] } });
                    remaining = 0;
                    truncated = true;
                }
            }

            if (hunks.Count < file.Hunks.Count)
            {
                truncated = true;
            }

            kept.Add(file with { Hunks = hunks });
        }

        return kept;
    }

    private static UntrustedText CapText(UntrustedText text, int max, ref bool truncated)
    {
        if (text.Length <= max)
        {
            return text;
        }

        truncated = true;
        return text with { Text = text.Text[..max] };
    }

    /// <summary>Keeps the most recent lines that fit. A single newest line longer than the budget is cut to fit.</summary>
    private List<GcThreadLine> CapGcThread(IReadOnlyList<GcThreadLine> lines, ref bool truncated)
    {
        var budget = _limits.MaxGcThreadCharacters;
        var kept = new List<GcThreadLine>();
        var used = 0;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (used + line.Text.Length <= budget)
            {
                kept.Add(line);
                used += line.Text.Length;
                continue;
            }

            truncated = true;
            if (kept.Count == 0)
            {
                kept.Add(line with { Text = line.Text with { Text = line.Text.Text[..budget] } });
            }

            break;
        }

        kept.Reverse();
        return kept;
    }
}
