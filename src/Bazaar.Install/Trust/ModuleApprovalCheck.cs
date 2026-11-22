using System.Security.Cryptography;
using Bazaar.Install.Abstractions;

namespace Bazaar.Install.Trust;

/// <summary>
/// Works out which permissions the owner really approved for a package, from the stored approval rows (doc 48 section 1).
/// A row counts only when all of these hold: it is for this module id; rebuilding the approval form from the row and the
/// package's publisher gives the digest the row stores; and the owner signature verifies over that form. A row that fails
/// any check is ignored, so writing to the approval table is not enough to grant a permission, and an approval given to one
/// publisher's module does not carry to another publisher. Rows of earlier versions count, which is what lets a narrowed
/// update start without a new approval.
/// </summary>
public static class ModuleApprovalCheck
{
    /// <summary>The union of the permissions in every valid approval row for <paramref name="package"/>.</summary>
    public static IReadOnlySet<string> ApprovedFor(
        ModulePackage package,
        IEnumerable<ModulePermissionApprovalRecord> records,
        IOwnerApprovalVerifier ownerVerifier)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(ownerVerifier);

        var approved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (IsValid(package, record, ownerVerifier))
            {
                approved.UnionWith(record.Permissions);
            }
        }

        return approved;
    }

    private static bool IsValid(ModulePackage package, ModulePermissionApprovalRecord record, IOwnerApprovalVerifier ownerVerifier)
    {
        if (record is null
            || !string.Equals(record.ModuleId, package.ModuleId, StringComparison.Ordinal)
            || !ModuleText.IsToken(record.Version)
            || !ModuleText.IsToken(record.SignerKeyId)
            || record.Signature is not { Length: > 0 }
            || record.Permissions is not { Count: > 0 and <= ModuleText.MaxPermissions }
            || !record.Permissions.All(ModuleText.IsToken)
            || !ModuleText.IsToken(package.PublisherId))
        {
            return false;
        }

        var form = ModuleApprovalForm.Build(record.ModuleId, record.Version, package.PublisherId, record.Permissions);
        var digest = Convert.ToHexStringLower(SHA256.HashData(form));
        if (!string.Equals(digest, record.ApprovalDigestSha256Hex, StringComparison.Ordinal))
        {
            return false;
        }

        return ownerVerifier.Verify(form, new OwnerApprovalSignature(record.SignerKeyId, record.Signature));
    }
}
