namespace Bazaar.Server.Modules.Trust;

/// <summary>The result of comparing a module version's declared permissions with the owner's approval.</summary>
/// <param name="IsApproved">True when every declared permission is covered by the approval.</param>
/// <param name="Unapproved">The declared permissions the approval does not cover, sorted ordinally. Empty when approved.</param>
public sealed record PermissionApprovalResult(bool IsApproved, IReadOnlyList<string> Unapproved);

/// <summary>
/// Permission approval per module version (doc 48 section 1). The owner approves the declared set once per version.
/// A version is approved only when its declared set is covered by the approved set. Narrowing needs no new approval,
/// because the declared set is then a subset of what the owner already approved. Widening is never silently approved:
/// the added permissions come back in <see cref="PermissionApprovalResult.Unapproved"/>.
/// </summary>
public static class PermissionApproval
{
    /// <summary>
    /// Compares the declared permissions with the approved set. The approved set must be the set the owner approved for
    /// this module version or the last approved version; carrying an approval forward unchanged is what makes widening visible.
    /// </summary>
    public static PermissionApprovalResult Evaluate(IReadOnlySet<string> declared, IReadOnlySet<string> approved)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(approved);

        var unapproved = declared
            .Where(permission => !approved.Contains(permission))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new PermissionApprovalResult(unapproved.Count == 0, unapproved);
    }
}
