using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Modules.Persistence;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Modules.Install;

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

    /// <summary>A permission name is blank.</summary>
    public sealed record RefusedMalformed(string Reason) : ModuleApprovalResult;

    /// <summary>No owner device key signed the approval (none enrolled, revoked, or locked). Nothing is recorded unsigned.</summary>
    public sealed record RefusedNoOwnerSignature : ModuleApprovalResult;
}

/// <summary>
/// Records the owner's approval of a module version's permission set (doc 48 section 1). The approval form is signed by the
/// owner device key through <see cref="IOwnerApprovalSigner"/>. The signed row and its <c>module.permissions.approved</c> audit
/// entry commit in one transaction. The signer is required: there is no path that records an approval without a signature.
/// </summary>
public sealed class ModuleApprovalService(
    ModuleManagerRepository repository,
    ITransactionalAuditLog audit,
    IOwnerApprovalSigner signer,
    TimeProvider time)
{
    /// <summary>Approves the given permissions for one module version, signed by the owner.</summary>
    public async Task<ModuleApprovalResult> ApproveAsync(
        string moduleId,
        string version,
        IEnumerable<string> permissions,
        string ownerActor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActor);
        ArgumentNullException.ThrowIfNull(permissions);

        var requested = permissions.ToList();
        if (requested.Count == 0)
        {
            return new ModuleApprovalResult.RefusedEmpty();
        }

        if (requested.Any(string.IsNullOrWhiteSpace))
        {
            return new ModuleApprovalResult.RefusedMalformed("A permission name is blank.");
        }

        var approved = requested.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var form = ModuleApprovalForm.Build(moduleId, version, approved);

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
            ["signer"] = signed.SignerKeyId,
            ["version"] = version,
        };
        AuditPayloadGuard.Ensure(payload);
        var entry = new AuditEntry(at, ownerActor, ModuleAuditKinds.PermissionsApproved, payload.ToJsonString());

        await repository.AppendPermissionApprovalAsync(record, audit, entry, ct).ConfigureAwait(false);
        return new ModuleApprovalResult.Recorded(record);
    }
}
