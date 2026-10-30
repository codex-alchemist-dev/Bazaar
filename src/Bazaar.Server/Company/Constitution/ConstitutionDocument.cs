// TODO(component): move to the Company component project when PR #125 merges.
namespace Bazaar.Server.Company.Constitution;

/// <summary>One section of a Constitution: its level-2 heading and the Markdown body under it.</summary>
/// <param name="Heading">The heading text, without the <c>## </c> marker.</param>
/// <param name="Body">The body text, trimmed of leading and trailing whitespace.</param>
public sealed record ConstitutionSection(string Heading, string Body);

/// <summary>
/// A parsed Constitution (doc 24, section 1). Sections keep their document order. Use
/// <see cref="RequiredHeadings"/> for the headings every accepted commit must contain.
/// </summary>
/// <param name="Sections">Sections in the order they appear in the document.</param>
public sealed record ConstitutionDocument(IReadOnlyList<ConstitutionSection> Sections)
{
    /// <summary>The heading whose body holds the links to decision records.</summary>
    public const string DecisionIndexHeading = "Decision index";

    /// <summary>Headings that must appear exactly once, with exact (case-sensitive) names.</summary>
    public static IReadOnlyList<string> RequiredHeadings { get; } =
        ["Mission", "Vision", "Culture", "Engineering principles", "Best practices", DecisionIndexHeading];

    /// <summary>Returns the section with the given exact heading, or null when absent.</summary>
    /// <param name="heading">Heading text to find.</param>
    public ConstitutionSection? FindSection(string heading) =>
        Sections.FirstOrDefault(section => string.Equals(section.Heading, heading, StringComparison.Ordinal));
}
