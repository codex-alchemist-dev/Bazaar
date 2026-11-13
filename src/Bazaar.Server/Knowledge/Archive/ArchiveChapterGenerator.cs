using System.Text.RegularExpressions;

namespace Bazaar.Server.Knowledge.Archive;

/// <summary>Input for one chapter draft. The window is explicit, so the generator never reads a clock.</summary>
/// <param name="WindowStart">Inclusive start of the cadence window (UTC).</param>
/// <param name="WindowEnd">Exclusive end of the cadence window (UTC).</param>
/// <param name="Sources">Candidate sources. Anything outside the window is ignored; restricted kinds are dropped later by the validator.</param>
/// <param name="PredecessorPath">The chapter this one regenerates, or null.</param>
public sealed record ArchiveChapterRequest(
    Guid DocId,
    string Project,
    string Slug,
    string Title,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IEnumerable<ArchiveSource> Sources,
    string? PredecessorPath);

/// <summary>
/// Builds a chapter draft: one sentence per source entry in the window, in time order, each citing that entry (doc 25 WP6).
/// Pure and deterministic. It calls no model; prose wording is out of scope here. Thinking and excluded text are not
/// removed here; the validator is the single place that drops them, so the floors cannot be bypassed by a caller.
/// </summary>
public static class ArchiveChapterGenerator
{
    public static ArchiveChapterDraft Generate(ArchiveChapterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArchivePaths.ValidateSegment(request.Project, nameof(request.Project));
        ArchivePaths.ValidateSegment(request.Slug, nameof(request.Slug));
        ArchivePaths.ValidateTitle(request.Title);
        ArgumentNullException.ThrowIfNull(request.Sources);
        if (request.WindowEnd <= request.WindowStart)
        {
            throw new ArgumentException("The window end must be after its start.", nameof(request));
        }

        var chapterPath = ArchivePaths.ChapterPath(request.Project, request.Slug);
        if (request.PredecessorPath == chapterPath)
        {
            throw new ArgumentException("A chapter cannot be its own predecessor.", nameof(request));
        }

        var inWindow = request.Sources
            .Where(source => source.At >= request.WindowStart && source.At < request.WindowEnd)
            .ToList();
        ArchiveSourceIndex.Build(inWindow);

        var sentences = inWindow
            .OrderBy(source => source.At)
            .ThenBy(source => source.Ref, StringComparer.Ordinal)
            .Select(source => new ArchiveSentence(CollapseWhitespace(source.Text), source.Ref))
            .ToList();

        return new ArchiveChapterDraft(
            DocId: request.DocId,
            Project: request.Project,
            Slug: request.Slug,
            Title: request.Title,
            Sentences: sentences,
            PredecessorPath: request.PredecessorPath);
    }

    private static string CollapseWhitespace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Vault paths and name rules for the Archive (doc 18 folder <c>archive/</c>).</summary>
public static partial class ArchivePaths
{
    /// <summary>The Vault folder holding every chapter.</summary>
    public const string Folder = "archive";

    /// <summary>The Vault path of a chapter: <c>archive/&lt;project&gt;/&lt;slug&gt;.md</c>.</summary>
    public static string ChapterPath(string project, string slug)
    {
        ValidateSegment(project, nameof(project));
        ValidateSegment(slug, nameof(slug));
        return $"{Folder}/{project}/{slug}.md";
    }

    /// <summary>A path segment: lowercase letters, digits and single hyphens.</summary>
    internal static void ValidateSegment(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (!SegmentPattern().IsMatch(value))
        {
            throw new ArgumentException("Must be lowercase letters, digits and single hyphens.", paramName);
        }
    }

    /// <summary>Front matter titles may not contain # | ^ : %% [[ ]] (doc 18).</summary>
    internal static void ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.IndexOfAny(['#', '|', '^', ':']) >= 0 || title.Contains("%%", StringComparison.Ordinal)
            || title.Contains("[[", StringComparison.Ordinal) || title.Contains("]]", StringComparison.Ordinal))
        {
            throw new ArgumentException("Title contains a character that front matter forbids (# | ^ : %% [[ ]]).", nameof(title));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
