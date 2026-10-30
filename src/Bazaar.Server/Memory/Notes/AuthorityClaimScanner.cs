using System.Text.RegularExpressions;

namespace Bazaar.Server.Memory.Notes;

/// <summary>
/// Finds text that claims a role, permission or approval (docs/design/08-memory-system.md, section 2,
/// step 2). Such text is not rejected: it is stored at T1 and flagged, and it never changes the Soul.
/// </summary>
public static partial class AuthorityClaimScanner
{
    /// <summary>True when the text claims an approval, permission, role or ownership.</summary>
    public static bool ClaimsAuthority(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Patterns().Any(p => p.IsMatch(text));
    }

    private static IEnumerable<Regex> Patterns()
    {
        yield return YouAreApproved();
        yield return ApprovedByOwner();
        yield return OwnerApproved();
        yield return PermissionGranted();
        yield return IAmTheOwner();
        yield return GrantedYouAccess();
        yield return NoteIsApproved();
    }

    [GeneratedRegex(@"\byou(?:\s+(?:are|have)|'re|'ve)\s+(?:now\s+|hereby\s+|fully\s+)*(?:approved|authori[sz]ed|permitted|promoted|allowed\s+to|the\s+owner|an?\s+admin(?:istrator)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YouAreApproved();

    [GeneratedRegex(@"\b(?:approved|authori[sz]ed|granted|cleared)\s+by\s+(?:the\s+)?(?:owner|admin(?:istrator)?|human)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApprovedByOwner();

    [GeneratedRegex(@"\b(?:owner|admin(?:istrator)?)\s+(?:has\s+)?(?:approved|authori[sz]ed|granted)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnerApproved();

    [GeneratedRegex(@"\bpermission\s+(?:is\s+|has\s+been\s+)?granted\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PermissionGranted();

    [GeneratedRegex(@"\bi\s+am\s+the\s+owner\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IAmTheOwner();

    [GeneratedRegex(@"\bgrant(?:ed)?\s+you\s+(?:permission|access|approval)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrantedYouAccess();

    [GeneratedRegex(@"\bthis\s+(?:note|memory)\s+is\s+(?:approved|authori[sz]ed|trusted|verified\s+by\s+the\s+owner)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteIsApproved();
}
