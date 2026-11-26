namespace Bazaar.Server.CompanyKnowledge.Abstractions;

/// <summary>
/// The six company-note kinds of docs/design/08-memory-system.md, section 6.2. The wire name is the folder under
/// <c>company/memory/</c> and the value of <c>company.kind</c>.
/// </summary>
public enum CompanyKind
{
    /// <summary>A rule the company holds to.</summary>
    Principle,

    /// <summary>Something the company found out, with the conditions it holds under.</summary>
    Learning,

    /// <summary>A practice, tool or approach that worked, with evidence.</summary>
    WhatWorks,

    /// <summary>A practice that failed, and why.</summary>
    WhatFails,

    /// <summary>A generalised decision pattern.</summary>
    Precedent,

    /// <summary>A generalised incident.</summary>
    Incident,
}

/// <summary>Wire names for <see cref="CompanyKind"/>.</summary>
public static class CompanyKinds
{
    /// <summary>The wire name, equal to the folder under <c>company/memory/</c>.</summary>
    public static string Name(CompanyKind kind) => kind switch
    {
        CompanyKind.Principle => "principle",
        CompanyKind.Learning => "learning",
        CompanyKind.WhatWorks => "what-works",
        CompanyKind.WhatFails => "what-fails",
        CompanyKind.Precedent => "precedent",
        CompanyKind.Incident => "incident",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown company kind."),
    };

    /// <summary>Parses a wire name. Matching is ordinal and case-sensitive.</summary>
    public static bool TryParse(string? text, out CompanyKind kind)
    {
        foreach (var candidate in Enum.GetValues<CompanyKind>())
        {
            if (string.Equals(Name(candidate), text, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>True for the failure kinds, which carry an <c>occurrences</c> record (doc 08, 6.6).</summary>
    public static bool TracksOccurrences(CompanyKind kind) => kind is CompanyKind.WhatFails or CompanyKind.Incident;
}
