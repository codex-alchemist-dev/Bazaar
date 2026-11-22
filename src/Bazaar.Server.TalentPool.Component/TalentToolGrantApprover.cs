using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>What happened to a tool-grant approval attempt.</summary>
public enum ToolGrantApprovalResult
{
    /// <summary>The owner's signature was valid and the grant is recorded.</summary>
    Approved,

    /// <summary>No such template version, or it asks for no tools.</summary>
    NothingToApprove,

    /// <summary>The payload is not a Talent tool-grant action, or its hash does not cover this exact template version and tool list.</summary>
    PayloadMismatch,

    /// <summary>The owner signature was refused. The grant is not recorded.</summary>
    SignatureRefused,
}

/// <summary>
/// The owner approval flow for a template's tool grants (docs/design/25, docs/design/15 Tier 2; owner signature per doc 32 section 5a).
/// The owner's device signs an <see cref="OwnerPayload"/> for action <see cref="Action"/> whose args hash covers the template version
/// and its exact tool list. Only a valid signature through <see cref="IOwnerSignatureVerifier"/> records the grant. A hire vote
/// never does.
/// </summary>
public sealed class TalentToolGrantApprover(
    ITalentTemplateStore templates,
    IOwnerSignatureVerifier verifier,
    IToolGrantRecorder approvals,
    TimeProvider time)
{
    /// <summary>The owner-signed action name.</summary>
    public const string Action = "talent.tool_grant";

    /// <summary>The args hash the owner's device must sign for a template version: SHA-256 of <c>hire_key</c>, a newline and the tool-list hash.</summary>
    public static byte[] ArgsHashFor(string hireKey, IReadOnlyList<string> tools) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(hireKey + "\n" + PostgresToolGrantApproval.ToolsSha(tools)));

    /// <summary>Verifies the owner's signed payload and records the approval for the stored tool list of that version.</summary>
    /// <returns>The outcome. Only <see cref="ToolGrantApprovalResult.Approved"/> records anything.</returns>
    public async Task<ToolGrantApprovalResult> ApproveAsync(
        string templateId, int version, OwnerPayload payload, byte[] signature, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);

        var template = await templates.FindAsync(templateId, version, ct).ConfigureAwait(false);
        if (template is null || template.Manifest.ToolAllowlist.Count == 0)
        {
            return ToolGrantApprovalResult.NothingToApprove;
        }

        var expected = ArgsHashFor(template.HireKey, template.Manifest.ToolAllowlist);
        if (payload.Action != Action || !CryptographicOperations.FixedTimeEquals(payload.ArgsHash, expected))
        {
            return ToolGrantApprovalResult.PayloadMismatch;
        }

        var verdict = await verifier.VerifyAsync(payload, signature, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (verdict != OwnerSignatureVerdict.Valid)
        {
            return ToolGrantApprovalResult.SignatureRefused;
        }

        await approvals.RecordAsync(template.HireKey, template.Manifest.ToolAllowlist, payload.HumanId.ToString("D"), ct).ConfigureAwait(false);
        return ToolGrantApprovalResult.Approved;
    }
}
