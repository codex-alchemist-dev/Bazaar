using Bazaar.Server.CompanyKnowledge.Abstractions;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>Derived evidence counts and confidence (docs/design/08-memory-system.md, 6.6 and 6.7). Pure; written by the server, never a proposer.</summary>
public static class CompanyConfidence
{
    /// <summary>
    /// Counts from the evidence: <c>projects_confirming</c> is the distinct projects with at least one live, independent, confirming entry;
    /// <c>projects_contradicting</c> likewise for contradicting entries; <c>sources_tombstoned</c> counts tombstones. Several notes of one project count once.
    /// </summary>
    public static CompanyCounts CountsFor(IReadOnlyList<CompanyEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        int Projects(CompanyEvidenceRole role) => evidence
            .Where(e => e.Role == role && e.Status == CompanyEvidenceStatus.Live && e.Independent && !string.IsNullOrWhiteSpace(e.Project))
            .Select(e => e.Project!)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new CompanyCounts(
            Projects(CompanyEvidenceRole.Confirms),
            Projects(CompanyEvidenceRole.Contradicts),
            evidence.Count(e => e.Status == CompanyEvidenceStatus.Tombstone));
    }

    /// <summary>
    /// The derived confidence: <c>expired</c> past <c>valid_to</c>, <c>contested</c> on a live contradiction (evidence or an active note linked
    /// <c>contradicts</c>), <c>stale</c> past <c>review_by</c>, <c>confirmed</c> at <paramref name="confirmedMinProjects"/> confirming projects,
    /// otherwise <c>single-project</c>. It changes the label and ranking only, never the tier.
    /// </summary>
    public static string Derive(CompanyCounts counts, DateOnly? reviewBy, DateOnly? validTo, DateOnly today, int confirmedMinProjects, bool linkedContradiction)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (validTo is { } end && end < today)
        {
            return "expired";
        }

        if (linkedContradiction || counts.ProjectsContradicting > 0)
        {
            return "contested";
        }

        if (reviewBy is { } review && review < today)
        {
            return "stale";
        }

        return counts.ProjectsConfirming >= confirmedMinProjects ? "confirmed" : "single-project";
    }
}
