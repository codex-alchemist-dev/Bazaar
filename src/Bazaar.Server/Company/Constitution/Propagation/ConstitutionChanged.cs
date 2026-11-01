// TODO(component): move to the Company component project when PR #125 merges.
namespace Bazaar.Server.Company.Constitution.Propagation;

/// <summary>
/// The <c>constitution.changed</c> event (doc 24, section 1, Propagation): an accepted commit changed the Constitution.
/// </summary>
public sealed record ConstitutionChanged
{
    /// <summary>Creates the event after checking every field.</summary>
    /// <param name="from">The commit the Constitution was at before, or null for the first activation.</param>
    /// <param name="to">The accepted commit now in force.</param>
    /// <param name="sections">The names of the changed sections; at least one, non-blank and distinct.</param>
    /// <exception cref="ArgumentException">A sha is not 40 lowercase hex characters, <paramref name="from"/> equals
    /// <paramref name="to"/>, or the section list is empty, blank or has duplicates.</exception>
    public ConstitutionChanged(string? from, string to, IReadOnlyList<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (from is not null && !IsValidSha(from))
        {
            throw new ArgumentException("The 'from' commit must be a 40-character lowercase sha.", nameof(from));
        }

        if (!IsValidSha(to))
        {
            throw new ArgumentException("The 'to' commit must be a 40-character lowercase sha.", nameof(to));
        }

        if (from == to)
        {
            throw new ArgumentException("The 'from' and 'to' commits must differ.", nameof(to));
        }

        if (sections.Count == 0)
        {
            throw new ArgumentException("At least one changed section is required.", nameof(sections));
        }

        if (sections.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Section names must not be blank.", nameof(sections));
        }

        if (sections.Distinct(StringComparer.Ordinal).Count() != sections.Count)
        {
            throw new ArgumentException("Section names must be distinct.", nameof(sections));
        }

        From = from;
        To = to;
        Sections = sections.ToArray();
    }

    /// <summary>The commit before the change, or null for the first activation.</summary>
    public string? From { get; }

    /// <summary>The accepted commit now in force.</summary>
    public string To { get; }

    /// <summary>The changed section names, in the order given.</summary>
    public IReadOnlyList<string> Sections { get; }

    /// <summary>True when <paramref name="sha"/> is 40 lowercase hex characters.</summary>
    public static bool IsValidSha(string? sha)
    {
        if (sha is null || sha.Length != 40)
        {
            return false;
        }

        foreach (var c in sha)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
