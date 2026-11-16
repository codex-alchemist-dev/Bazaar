namespace Bazaar.Install.Abstractions;

/// <summary>The owner's signature over one approval form, from the owner device key (doc 48 section 1, doc 32 section 5a).</summary>
/// <param name="SignerKeyId">The owner device key id that produced the signature.</param>
/// <param name="Signature">The signature bytes over the approval form.</param>
public sealed record OwnerApprovalSignature(string SignerKeyId, byte[] Signature);

/// <summary>
/// Signs module permission approvals with the owner's device key. No production implementation exists yet: the owner approval
/// key (doc 32 section 5a) is PROPOSED and has no signer in this repo. Implementations must return null when no key can sign
/// (not enrolled, revoked, or locked). They must never return a placeholder signature. <see cref="ModuleApprovalService"/>
/// refuses a null or empty result, so an approval is never recorded without a real signature.
/// </summary>
public interface IOwnerApprovalSigner
{
    /// <summary>Signs the approval form for <paramref name="ownerActor"/>. Returns null when no owner device key can sign.</summary>
    Task<OwnerApprovalSignature?> SignAsync(ReadOnlyMemory<byte> approvalForm, string ownerActor, CancellationToken ct = default);
}
