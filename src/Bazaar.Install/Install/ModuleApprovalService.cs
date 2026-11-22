using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;

namespace Bazaar.Install;

/// <summary>The outcome of <see cref="ModuleApprovalService.ApproveAsync"/>. Every refusal records nothing.</summary>
public abstract record ModuleApprovalResult
{
    private ModuleApprovalResult()
    {
    }

    /// <summary>The approval is signed, stored and audited.</summary>
    public sealed record Recorded(ModulePermissionApprovalRecord Approval) : ModuleApprovalResult;

    /// <summary>The owner approved no permissions. An empty approval is never recorded.</summary>
    public sealed record RefusedEmpty : ModuleApprovalResult;

    /// <summary>A permission name is not valid text, or the package does not declare it.</summary>
    public sealed record RefusedMalformed(string Reason) : ModuleApprovalResult;

    /// <summary>The package signature does not verify. The owner is never asked to sign for a package of unknown origin.</summary>
    public sealed record RefusedSignature(ModuleSignatureStatus Status) : ModuleApprovalResult;

    /// <summary>No owner device key signed the approval (none enrolled, revoked, or locked). Nothing is recorded unsigned.</summary>
    public sealed record RefusedNoOwnerSignature : ModuleApprovalResult;
}

/// <summary>
/// Records the owner's approval of a module version's permission set (doc 48 section 1). The package signature is verified
/// first, and only permissions the package declares can be approved. The approval form is signed by the
/// owner device key through <see cref="IOwnerApprovalSigner"/>. The signed row and its <c>module.permissions.approved</c> audit
/// entry commit in one transaction. The signer is required: there is no path that records an approval without a signature.
/// </summary>
public sealed class ModuleApprovalService(
    IModuleSignatureVerifier verifier,
    IModuleInstallStore store,
    IOwnerApprovalSigner signer,
    TimeProvider time)
{
    /// <summary>Approves the given permissions for one verified package, signed by the owner.</summary>
    public async Task<ModuleApprovalResult> ApproveAsync(
        ModulePackage package,
        IEnumerable<string> permissions,
        string ownerActor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActor);
        ArgumentNullException.ThrowIfNull(permissions);

        var status = verifier.Verify(package);
        if (status != ModuleSignatureStatus.Valid)
        {
            return new ModuleApprovalResult.RefusedSignature(status);
        }

        var (moduleId, version) = (package.ModuleId, package.Version);
        var requested = permissions.ToList();
        if (requested.Count == 0)
        {
            return new ModuleApprovalResult.RefusedEmpty();
        }

        if (requested.Count > ModuleText.MaxPermissions || !requested.All(ModuleText.IsToken))
        {
            return new ModuleApprovalResult.RefusedMalformed("A permission name is blank, too long or not printable ASCII.");
        }

        if (requested.Any(permission => !package.DeclaredPermissions.Contains(permission)))
        {
            return new ModuleApprovalResult.RefusedMalformed("A permission is not one the package declares.");
        }

        var approved = requested.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var form = ModuleApprovalForm.Build(moduleId, version, package.PublisherId, approved);

        var signed = await signer.SignAsync(form, ownerActor, ct).ConfigureAwait(false);
        if (signed is null || string.IsNullOrWhiteSpace(signed.SignerKeyId) || signed.Signature.Length == 0)
        {
            return new ModuleApprovalResult.RefusedNoOwnerSignature();
        }

        var at = time.GetUtcNow();
        var digest = Convert.ToHexStringLower(SHA256.HashData(form));
        var record = new ModulePermissionApprovalRecord(
            ModuleId: moduleId,
            Version: version,
            Permissions: approved,
            ApprovalDigestSha256Hex: digest,
            SignerKeyId: signed.SignerKeyId,
            Signature: signed.Signature,
            OwnerActor: ownerActor,
            ApprovedAt: at);

        var payload = new JsonObject
        {
            ["approval_digest"] = digest,
            ["module_id"] = moduleId,
            ["permissions"] = new JsonArray([.. approved.Select(p => (JsonNode)p)]),
            ["publisher"] = package.PublisherId,
            ["signer"] = signed.SignerKeyId,
            ["version"] = version,
        };
        var entry = new InstallAuditEntry(at, ownerActor, ModuleAuditKinds.PermissionsApproved, payload);

        await store.RecordPermissionApprovalAsync(record, entry, ct).ConfigureAwait(false);
        return new ModuleApprovalResult.Recorded(record);
    }
}
