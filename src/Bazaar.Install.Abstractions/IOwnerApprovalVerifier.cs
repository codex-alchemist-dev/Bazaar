namespace Bazaar.Install.Abstractions;

/// <summary>
/// Checks an owner signature over an approval form against the owner's enrolled device keys (doc 48 section 1: the approval is
/// "signed by the owner's device key"). The install and start gates call it for every stored approval, so a row that was
/// written into the approval table by anything but the owner's key counts for nothing. Like <see cref="IOwnerApprovalSigner"/>
/// it has no production implementation yet (the owner approval key, doc 32 section 5a, is PROPOSED). Implementations must
/// return false for an unknown, revoked or expired key and for any error; they must never throw for a bad signature.
/// </summary>
public interface IOwnerApprovalVerifier
{
    /// <summary>True only when <paramref name="signature"/> is a valid owner signature over exactly <paramref name="approvalForm"/>.</summary>
    bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature);
}
