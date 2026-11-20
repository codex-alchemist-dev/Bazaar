using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals;

/// <summary>
/// Signs the canonical approval payload with the device's approval key (doc 32 section 5a). The key is non-exportable and
/// never authenticates a session. The Android head implements it over the Keystore; enrolment is not part of this interface
/// (doc 32 Question 4 is open), so a signer is handed an already enrolled key.
/// </summary>
public interface IDeviceApprovalSigner
{
    /// <summary>Id of the enrolled key, as the server knows it. Must match <c>^[A-Za-z0-9._-]{1,128}$</c>.</summary>
    string DeviceId { get; }

    /// <summary>
    /// Signs <paramref name="canonicalPayload"/> with ECDSA P-256 and SHA-256 and returns the 64-byte IEEE P1363 signature.
    /// </summary>
    ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken);
}

/// <summary>Sends a signed decision to the server. The gate is the only caller, and it calls this only after a fresh biometric success and a signature.</summary>
public interface IApprovalDecisionSender
{
    /// <summary>Sends the decision over the authenticated connection.</summary>
    ValueTask SendAsync(ApprovalDecisionDto decision, CancellationToken cancellationToken);
}
