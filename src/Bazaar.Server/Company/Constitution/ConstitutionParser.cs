// TODO(component): move to the Company component project when PR #125 merges.
using System.Text.RegularExpressions;

namespace Bazaar.Server.Company.Constitution;

/// <summary>Why a Constitution text was refused.</summary>
public enum ConstitutionParseErrorKind
{
    /// <summary>The text is null, empty or only whitespace.</summary>
    EmptyInput,

    /// <summary>A required heading does not appear.</summary>
    MissingRequiredHeading,

    /// <summary>A heading appears more than once.</summary>
    DuplicateHeading,

    /// <summary>A required section has no body text.</summary>
    EmptyRequiredSection,
}

/// <summary>One reason a Constitution text was refused.</summary>
/// <param name="Kind">The category of the problem.</param>
/// <param name="Message">A human-readable description, including line numbers where they apply.</param>
public sealed record ConstitutionParseError(ConstitutionParseErrorKind Kind, string Message);

/// <summary>Outcome of parsing a Constitution text. On failure, <see cref="Document"/> is null.</summary>
/// <param name="Success">True when the text parsed and passed every check.</param>
/// <param name="Document">The parsed document, or null on failure.</param>
/// <param name="Errors">Every problem found on failure; empty on success.</param>
public sealed record ConstitutionParseResult(
    bool Success,
    ConstitutionDocument? Document,
    IReadOnlyList<ConstitutionParseError> Errors)
{
    /// <summary>Builds a successful result.</summary>
    /// <param name="document">The parsed document.</param>
    public static ConstitutionParseResult Ok(ConstitutionDocument document) => new(true, document, []);

    /// <summary>Builds a failed result.</summary>
    /// <param name="errors">The problems that refused the text.</param>
    public static ConstitutionParseResult Failed(IReadOnlyList<ConstitutionParseError> errors) => new(false, null, errors);
}

/// <summary>
/// Pure parser for Constitution Markdown (doc 24, section 1). Each section starts at a level-2
/// heading (<c>## Name</c>). No I/O: the caller loads the text and decides what to do with the result.
/// </summary>
/// <remarks>
/// Bad input never throws; it returns a failed <see cref="ConstitutionParseResult"/>. Rules:
/// <list type="bullet">
/// <item>Headings inside fenced code blocks are body text, not sections.</item>
/// <item>Text before the first section (for example a level-1 title) is ignored.</item>
/// <item>Each required heading must appear exactly once. A repeated heading of any name is refused.</item>
/// <item>A required section with an empty body is refused.</item>
/// <item>Extra sections are kept in document order; their order and names are not checked.</item>
/// </list>
/// All problems are collected, not just the first.
/// </remarks>
public static partial class ConstitutionParser
{
    /// <summary>Parses Constitution Markdown text.</summary>
    /// <param name="markdown">The full text of the Constitution file, or null.</param>
    public static ConstitutionParseResult Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return ConstitutionParseResult.Failed(
            [
                new ConstitutionParseError(ConstitutionParseErrorKind.EmptyInput, "Constitution text is empty."),
            ]);
        }

        var sections = ReadSections(markdown);
        var errors = new List<ConstitutionParseError>();

        foreach (var group in sections.GroupBy(section => section.Heading, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                var lines = string.Join(", ", group.Select(section => section.Line));
                errors.Add(new ConstitutionParseError(
                    ConstitutionParseErrorKind.DuplicateHeading,
                    $"Heading '{group.Key}' appears {group.Count()} times (lines {lines})."));
            }
        }

        foreach (var required in ConstitutionDocument.RequiredHeadings)
        {
            var match = sections.FirstOrDefault(section => string.Equals(section.Heading, required, StringComparison.Ordinal));
            if (match is null)
            {
                errors.Add(new ConstitutionParseError(
                    ConstitutionParseErrorKind.MissingRequiredHeading,
                    $"Required heading '{required}' is missing."));
            }
            else if (string.IsNullOrWhiteSpace(string.Join('\n', match.Body)))
            {
                errors.Add(new ConstitutionParseError(
                    ConstitutionParseErrorKind.EmptyRequiredSection,
                    $"Required section '{required}' (line {match.Line}) has no body text."));
            }
        }

        if (errors.Count > 0)
        {
            return ConstitutionParseResult.Failed(errors);
        }

        var document = new ConstitutionDocument(
            sections.Select(section => new ConstitutionSection(section.Heading, string.Join('\n', section.Body).Trim()))
                .ToList());
        return ConstitutionParseResult.Ok(document);
    }

    /// <summary>Splits the text into raw sections, tracking fenced code blocks.</summary>
    private static List<RawSection> ReadSections(string markdown)
    {
        var sections = new List<RawSection>();
        RawSection? current = null;
        var inFence = false;
        var lines = markdown.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence)
            {
                var match = SectionHeading().Match(line);
                if (match.Success)
                {
                    current = new RawSection(match.Groups[1].Value, i + 1, []);
                    sections.Add(current);
                    continue;
                }
            }

            current?.Body.Add(line);
        }

        return sections;
    }

    /// <summary>A section as read from the text, before the checks run.</summary>
    /// <param name="Heading">Heading text.</param>
    /// <param name="Line">One-based line number of the heading.</param>
    /// <param name="Body">Body lines, in order.</param>
    private sealed record RawSection(string Heading, int Line, List<string> Body);

    /// <summary>A level-2 heading: exactly two hashes followed by whitespace and text.</summary>
    [GeneratedRegex(@"^##[ \t]+(\S.*?)[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeading();
}
