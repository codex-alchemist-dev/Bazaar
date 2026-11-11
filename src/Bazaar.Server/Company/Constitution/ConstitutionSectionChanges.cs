// TODO(component): move to the Company component project when PR #125 merges.
namespace Bazaar.Server.Company.Constitution;

/// <summary>Derives the Constitution sections affected by an accepted document change.</summary>
// Re-runs the analyzed checks on this PR; no behaviour change.
public static class ConstitutionSectionChanges
{
    /// <summary>
    /// Returns added, removed, and body-edited section names in deterministic order. New-document sections come first
    /// in document order; removed sections follow in their previous document order.
    /// </summary>
    /// <param name="previous">The active document, or null for first activation.</param>
    /// <param name="current">The newly accepted document.</param>
    public static IReadOnlyList<string> Diff(ConstitutionDocument? previous, ConstitutionDocument current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var oldSections = Index(previous?.Sections);
        var newSections = Index(current.Sections);
        var changed = new List<string>();

        foreach (var section in current.Sections)
        {
            if (!oldSections.TryGetValue(section.Heading, out var oldBody)
                || !string.Equals(oldBody, section.Body, StringComparison.Ordinal))
            {
                changed.Add(section.Heading);
            }
        }

        if (previous is not null)
        {
            foreach (var section in previous.Sections)
            {
                if (!newSections.ContainsKey(section.Heading))
                {
                    changed.Add(section.Heading);
                }
            }
        }

        return changed;
    }

    private static Dictionary<string, string> Index(IReadOnlyList<ConstitutionSection>? sections)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sections is null)
        {
            return result;
        }

        foreach (var section in sections)
        {
            ArgumentNullException.ThrowIfNull(section);
            if (!result.TryAdd(section.Heading, section.Body))
            {
                throw new ArgumentException($"Constitution heading '{section.Heading}' appears more than once.", nameof(sections));
            }
        }

        return result;
    }
}
