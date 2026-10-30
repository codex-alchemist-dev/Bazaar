// TODO(component): move to the Company component project when PR #125 merges.
using System.Text.RegularExpressions;

namespace Bazaar.Server.Company.Constitution;

/// <summary>
/// Extracts links to decision records from the Constitution's "Decision index" section (doc 24,
/// section 1). Pure: no I/O, no file checks.
/// </summary>
public static partial class ConstitutionDecisionLinks
{
    /// <summary>
    /// Returns each distinct <c>decisions/NNNN-slug.md</c> path in the Decision index, in order of first
    /// appearance. Other forms (wrong digit count, uppercase or underscore slugs, other folders) are ignored.
    /// </summary>
    /// <param name="document">A parsed Constitution.</param>
    public static IReadOnlyList<string> Extract(ConstitutionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var section = document.FindSection(ConstitutionDocument.DecisionIndexHeading);
        if (section is null)
        {
            return [];
        }

        var links = new List<string>();
        foreach (Match match in DecisionLink().Matches(section.Body))
        {
            if (!links.Contains(match.Value, StringComparer.Ordinal))
            {
                links.Add(match.Value);
            }
        }

        return links;
    }

    /// <summary>Four digits, a hyphen, a lowercase kebab-case slug, and <c>.md</c>, under a <c>decisions/</c> folder.</summary>
    [GeneratedRegex(@"(?<![\w/.\-])decisions/[0-9]{4}-[a-z0-9]+(?:-[a-z0-9]+)*\.md(?![\w.\-])", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionLink();
}
