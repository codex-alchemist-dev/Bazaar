using Bazaar.Erasure.Abstractions;

namespace Bazaar.Server.Retention;

/// <summary>
/// Decides whether any active legal hold covers a record (doc 48 section 9: a hold names a person, a GC, a company or a
/// category, and blocks purge in its scope). Legal hold always wins, so anything this does not recognise counts as covered.
/// </summary>
public static class LegalHoldMatcher
{
    /// <summary>True when some hold in <paramref name="holds"/> covers the record. An unknown scope kind covers everything (fail closed).</summary>
    public static bool Covers(IReadOnlyList<ActiveLegalHold> holds, string category, RetentionRecordRef record)
    {
        foreach (var hold in holds)
        {
            switch (hold.ScopeKind)
            {
                case "company":
                    return true;
                case "category":
                    if (string.Equals(hold.ScopeValue, category, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;
                case "person":
                    if (record.PersonId is not null && string.Equals(hold.ScopeValue, record.PersonId, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;
                case "gc":
                    if (record.GcId is not null && string.Equals(hold.ScopeValue, record.GcId, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;
                default:
                    return true;
            }
        }

        return false;
    }
}
