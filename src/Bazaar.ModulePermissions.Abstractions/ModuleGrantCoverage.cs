namespace Bazaar.ModulePermissions;

/// <summary>
/// Decides whether a declared permission lies inside an approved set (doc 48 section 1: narrowing needs no new approval,
/// widening does). A declared string is covered when the approved set holds the same string, or when both it and an approved
/// string are valid permissions and the declared one allows nothing the approved one does not. A string that is not a valid
/// permission is covered only by the identical string, so nothing is ever read loosely.
/// </summary>
public static class ModuleGrantCoverage
{
    /// <summary>True when <paramref name="declared"/> is inside <paramref name="approved"/>.</summary>
    public static bool IsCovered(string declared, IReadOnlyCollection<string> approved)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(approved);

        if (approved.Contains(declared, StringComparer.Ordinal))
        {
            return true;
        }

        if (!ModuleGrant.TryParse(declared, out var wanted))
        {
            return false;
        }

        foreach (var text in approved)
        {
            if (ModuleGrant.TryParse(text, out var granted) && wanted.IsWithin(granted))
            {
                return true;
            }
        }

        return false;
    }
}
