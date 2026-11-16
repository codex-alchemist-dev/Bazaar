using Bazaar.Server.Modules.Updates;

namespace Bazaar.Server.Modules.Persistence;

/// <summary>
/// One update decision for one candidate, as stored in <c>module_update_decision</c> (<c>modules/0001</c>). Decisions are
/// append-only: a later decision for the same module adds a row and never replaces one.
/// </summary>
/// <param name="ModuleId">The module the candidate belongs to.</param>
/// <param name="Version">The candidate version, for example "18.1.2".</param>
/// <param name="Line">The major line the version belongs to.</param>
/// <param name="Decision">What the evaluator or a person decided.</param>
/// <param name="Reason">Why, in words a person can read.</param>
/// <param name="PolicyId">The policy that set the decision, or null when the default rules did.</param>
/// <param name="DecidedAt">When the decision was made.</param>
/// <param name="Actor">Who made it: the evaluator, or the person who approved or rejected.</param>
public sealed record ModuleUpdateDecisionRecord(
    string ModuleId,
    string Version,
    string Line,
    UpdateDecision Decision,
    string Reason,
    string? PolicyId,
    DateTimeOffset DecidedAt,
    string Actor);

/// <summary>
/// One owner approval of a module version's permission set, as stored in <c>module_permission_approval</c> (<c>modules/0002</c>).
/// The signature is the owner's over the approval form (<c>ModuleApprovalForm</c>); the digest is that form's SHA-256, for audit.
/// </summary>
/// <param name="ModuleId">The module the approval covers.</param>
/// <param name="Version">The module version the approval covers.</param>
/// <param name="Permissions">The approved permissions, sorted ordinally with no duplicates.</param>
/// <param name="ApprovalDigestSha256Hex">SHA-256 of the approval form, in lowercase hex.</param>
/// <param name="SignerKeyId">The owner device key id that signed the form.</param>
/// <param name="Signature">The owner's signature bytes over the approval form.</param>
/// <param name="OwnerActor">The owner who approved.</param>
/// <param name="ApprovedAt">When the approval was recorded.</param>
public sealed record ModulePermissionApprovalRecord(
    string ModuleId,
    string Version,
    IReadOnlyList<string> Permissions,
    string ApprovalDigestSha256Hex,
    string SignerKeyId,
    byte[] Signature,
    string OwnerActor,
    DateTimeOffset ApprovedAt);
