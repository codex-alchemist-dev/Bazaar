using Bazaar.Install.Abstractions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Placeholder owner approval verifier that accepts no signature. The owner approval key (doc 32 section 5a) is PROPOSED and has no
/// verifier yet, so no stored approval can count and no packaged module can start. Replaced when the key is enrolled and verified.
/// </summary>
public sealed class DenyAllOwnerApprovalVerifier : IOwnerApprovalVerifier
{
    public bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature) => false;
}
