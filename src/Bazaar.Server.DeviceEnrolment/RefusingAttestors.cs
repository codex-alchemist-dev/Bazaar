using Bazaar.Server.Biometric;

namespace Bazaar.Server.DeviceEnrolment;

/// <summary>
/// The shipped default approval-key attestor. No platform verifier (Android key attestation chain, Windows TPM attestation) is
/// built yet, and an attestation that cannot be verified is refused, never trusted (doc 32 section 5b step 4). Every call answers
/// "not verified", so enrolment ends in <see cref="EnrolmentVerdict.ApprovalKeyNotHardware"/> until a real attestor is registered.
/// </summary>
public sealed class RefusingApprovalKeyAttestor : IApprovalKeyAttestor
{
    /// <inheritdoc />
    public Task<ApprovalKeyAttestationResult> VerifyAsync(DeviceClass deviceClass, byte[] approvalPublicKeySpki, IReadOnlyList<byte[]> attestation, CancellationToken ct = default) =>
        Task.FromResult(new ApprovalKeyAttestationResult(Verified: false, HardwareBacked: false, UserAuthPerUse: false));
}

/// <summary>
/// The shipped default credential attestor. It never reports a valid proof, so enrolment ends in
/// <see cref="EnrolmentVerdict.CredentialRefused"/> until a real WebAuthn registration verifier is registered.
/// </summary>
public sealed class RefusingCredentialAttestor : ICredentialAttestor
{
    /// <inheritdoc />
    public Task<CredentialRegistration> VerifyAsync(byte[] credentialId, byte[] attestation, byte[] clientDataJson, byte[] expectedChallenge, CancellationToken ct = default) =>
        Task.FromResult(new CredentialRegistration(ProofValid: false, Attested: false, [], new HashSet<BiometricFactor>(), SingleDevice: false, SignCount: 0));
}
