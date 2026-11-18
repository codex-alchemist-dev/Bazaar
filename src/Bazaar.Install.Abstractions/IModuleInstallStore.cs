using System.Text.Json.Nodes;

namespace Bazaar.Install.Abstractions;

/// <summary>
/// The write seam for the module install gate (doc 48 section 1). The Install module decides; the host supplies a store that
/// persists the decision. Implementations run the audit payload guard before they append, so no secret-like key is stored.
/// </summary>
public interface IModuleInstallStore
{
    /// <summary>Records one <c>module.installed</c> entry. Completes when the audit row is committed.</summary>
    Task RecordInstalledAsync(InstallAuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Stores one signed permission approval and its audit entry in one transaction, so both commit or neither does.
    /// </summary>
    Task RecordPermissionApprovalAsync(ModulePermissionApprovalRecord record, InstallAuditEntry entry, CancellationToken ct = default);
}

/// <summary>An audit entry produced by the install gate. <see cref="Payload"/> is a JSON object, not text.</summary>
public sealed record InstallAuditEntry(DateTimeOffset Ts, string Actor, string Kind, JsonObject Payload);

/// <summary>One owner-signed permission approval for a module version (doc 48 section 1).</summary>
public sealed record ModulePermissionApprovalRecord(
    string ModuleId,
    string Version,
    IReadOnlyList<string> Permissions,
    string ApprovalDigestSha256Hex,
    string SignerKeyId,
    byte[] Signature,
    string OwnerActor,
    DateTimeOffset ApprovedAt);
