using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;

namespace Bazaar.Install;

/// <summary>The outcome of <see cref="ModuleStartGuard.EnsureStartable"/>. Anything but <see cref="Startable"/> must not start.</summary>
public abstract record ModuleStartDecision
{
    private ModuleStartDecision()
    {
    }

    /// <summary>The signature verifies and the approved set covers every declared permission.</summary>
    public sealed record Startable : ModuleStartDecision;

    /// <summary>The signature does not verify now. The module must not start.</summary>
    public sealed record RefusedSignature(ModuleSignatureStatus Status) : ModuleStartDecision;

    /// <summary>The declared permissions are not all approved. Carries the list.</summary>
    public sealed record RefusedUnapproved(IReadOnlyList<string> Unapproved) : ModuleStartDecision;
}

/// <summary>
/// The start gate (doc 48 section 1: verify on download, on install and on every start). Re-runs the signature check and the
/// approved-set check, so a key revoked or a package altered after install stops the module. The approved set is worked out
/// here from the stored approval rows, each checked against the owner's key; a caller cannot hand in a set of its own.
/// The host check is not repeated here; it ran at install (doc 50 section 5).
/// </summary>
public static class ModuleStartGuard
{
    /// <summary>Decides whether the package may start now. Never starts anything itself.</summary>
    public static ModuleStartDecision EnsureStartable(
        ModulePackage package,
        IModuleSignatureVerifier verifier,
        IEnumerable<ModulePermissionApprovalRecord> approvals,
        IOwnerApprovalVerifier ownerVerifier)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(ownerVerifier);

        var status = verifier.Verify(package);
        if (status != ModuleSignatureStatus.Valid)
        {
            return new ModuleStartDecision.RefusedSignature(status);
        }

        var approved = ModuleApprovalCheck.ApprovedFor(package, approvals, ownerVerifier);
        var permissions = PermissionApproval.Evaluate(package.DeclaredPermissions, approved);
        return permissions.IsApproved
            ? new ModuleStartDecision.Startable()
            : new ModuleStartDecision.RefusedUnapproved(permissions.Unapproved);
    }
}
