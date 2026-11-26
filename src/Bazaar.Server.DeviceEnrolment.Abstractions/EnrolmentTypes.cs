using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Server.Biometric;

namespace Bazaar.Server.DeviceEnrolment;

/// <summary>The kind of device being enrolled. Matches the <c>device.class</c> column.</summary>
public enum DeviceClass
{
    Windows,
    Linux,
    Android,
    Terminal,
    Other,
}

/// <summary>What the server hands a device to start the ceremony (doc 32 section 5b step 1).</summary>
/// <param name="Nonce">32 random bytes, single use, bound to one human and one device.</param>
/// <param name="ExpiresAt">When the nonce stops being accepted.</param>
public sealed record EnrolmentChallenge(byte[] Nonce, DateTimeOffset ExpiresAt);

/// <summary>Proof that the owner started this enrolment. Exactly one of the two shapes.</summary>
public abstract record EnrolmentStarter;

/// <summary>First device: the one-time code the server console showed.</summary>
public sealed record ConsoleCodeStarter(string Code) : EnrolmentStarter;

/// <summary>Further devices: an already enrolled device signs <see cref="EnrolmentArgs.Action"/> and proves a biometric check.</summary>
public sealed record EnrolledDeviceStarter(OwnerPayload Payload, byte[] Signature, BiometricAssertion Biometric) : EnrolmentStarter;

/// <summary>What the new device sends (doc 32 section 5b step 3). Every field is raw bytes as the platform produced them.</summary>
public sealed record EnrolmentRequest(
    Guid HumanId,
    Guid DeviceId,
    DeviceClass Class,
    byte[] Nonce,
    byte[] ApprovalPublicKeySpki,
    IReadOnlyList<byte[]> ApprovalKeyAttestation,
    byte[] CredentialId,
    byte[] CredentialAttestation,
    byte[] CredentialClientData,
    EnrolmentStarter Starter);

/// <summary>Why an enrolment was accepted or refused. Only <see cref="Enrolled"/> writes anything. Reasons carry no key material.</summary>
public enum EnrolmentVerdict
{
    Enrolled,
    Malformed,
    ChallengeInvalid,
    StarterRefused,
    DeviceUnknown,
    AlreadyEnrolled,
    ApprovalKeyShape,
    ApprovalKeyNotHardware,
    CredentialRefused,
    CredentialSynced,
}

/// <summary>The outcome of <see cref="IDeviceEnrolment.CompleteAsync"/>.</summary>
public sealed record EnrolmentOutcome(EnrolmentVerdict Verdict, bool CredentialAttested = false)
{
    public bool Succeeded => Verdict == EnrolmentVerdict.Enrolled;
}

/// <summary>The ceremony (doc 32 section 5b). Hostile input never throws; it returns a refusal.</summary>
public interface IDeviceEnrolment
{
    /// <summary>Issues the single-use enrolment nonce for a human and a device.</summary>
    Task<EnrolmentChallenge> BeginAsync(Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Issues the one-time console code for the first device of a human. The caller shows it once.</summary>
    Task<string> IssueConsoleCodeAsync(Guid humanId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Runs every check in order and, only when all pass, writes the approval key and the credential in one transaction.</summary>
    Task<EnrolmentOutcome> CompleteAsync(EnrolmentRequest request, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>What the approval key's own hardware attestation proved.</summary>
public sealed record ApprovalKeyAttestationResult(bool Verified, bool HardwareBacked, bool UserAuthPerUse);

/// <summary>Verifies the approval key's platform attestation (Android key attestation chain, Windows TPM attestation). One provider per platform.</summary>
public interface IApprovalKeyAttestor
{
    Task<ApprovalKeyAttestationResult> VerifyAsync(DeviceClass deviceClass, byte[] approvalPublicKeySpki, IReadOnlyList<byte[]> attestation, CancellationToken ct = default);
}

/// <summary>
/// What registering the platform credential proved. <paramref name="ProofValid"/> is false when the credential did not sign the
/// enrolment challenge. <paramref name="Attested"/> is false when no attestation chain verified; the credential then enrols with no
/// attested factors and never counts for a high-risk action.
/// </summary>
public sealed record CredentialRegistration(
    bool ProofValid,
    bool Attested,
    byte[] PublicKeySpki,
    IReadOnlySet<BiometricFactor> AttestedFactors,
    bool SingleDevice,
    uint SignCount);

/// <summary>Checks the credential registration and that it signed <paramref name="expectedChallenge"/>. One provider per platform.</summary>
public interface ICredentialAttestor
{
    Task<CredentialRegistration> VerifyAsync(byte[] credentialId, byte[] attestation, byte[] clientDataJson, byte[] expectedChallenge, CancellationToken ct = default);
}

/// <summary>Everything one enrolment writes, in one transaction.</summary>
public sealed record EnrolmentCommit(
    Guid HumanId,
    Guid DeviceId,
    DeviceClass Class,
    byte[] Nonce,
    byte[]? ConsoleCodeHash,
    byte[] ApprovalPublicKeySpki,
    BiometricCredential Credential,
    DateTimeOffset At);

/// <summary>Why a commit did not go through.</summary>
public enum EnrolmentCommitResult
{
    Committed,
    ChallengeSpent,
    CodeSpent,
    DeviceUnknown,
    AlreadyEnrolled,
}

/// <summary>Challenge and code state, and the one atomic write.</summary>
public interface IEnrolmentStore
{
    Task SaveChallengeAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset expiresAt, CancellationToken ct = default);

    Task SaveCodeAsync(byte[] codeHash, Guid humanId, DateTimeOffset expiresAt, CancellationToken ct = default);

    /// <summary>True while the nonce is unspent, unexpired and bound to this human and device.</summary>
    Task<bool> IsChallengeLiveAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>True while the code is unspent, unexpired and issued to this human.</summary>
    Task<bool> IsCodeLiveAsync(byte[] codeHash, Guid humanId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>True when the device row exists, belongs to the human, has this class and is not revoked.</summary>
    Task<bool> DeviceMatchesAsync(Guid deviceId, Guid humanId, DeviceClass deviceClass, CancellationToken ct = default);

    /// <summary>
    /// Spends the nonce (and the code, when given), inserts the approval key and the credential, and writes the audit row, all in one
    /// transaction. A device holds one live pair: a second commit for it returns <see cref="EnrolmentCommitResult.AlreadyEnrolled"/>.
    /// </summary>
    Task<EnrolmentCommitResult> CommitAsync(EnrolmentCommit commit, CancellationToken ct = default);
}

/// <summary>Records refused enrolments (doc 32 section 5b). Success is audited inside <see cref="IEnrolmentStore.CommitAsync"/>.</summary>
public interface IEnrolmentAudit
{
    Task RefusedAsync(Guid humanId, Guid deviceId, EnrolmentVerdict verdict, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>What the enrolled device signs to start a further device's enrolment, and the challenge the new credential signs.</summary>
public static class EnrolmentArgs
{
    /// <summary>The owner payload action for approving a further device.</summary>
    public const string Action = "device.enrol";

    private const string _challengeDomain = "bazaar.enrol.v1";

    /// <summary>SHA-256 over the new device id and the SHA-256 of its approval key, each length-prefixed.</summary>
    public static byte[] Hash(Guid deviceId, byte[] approvalPublicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(approvalPublicKeySpki);
        var sb = new StringBuilder();
        Append(sb, deviceId.ToString("D"));
        Append(sb, Convert.ToHexString(SHA256.HashData(approvalPublicKeySpki)));
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>
    /// The credential's WebAuthn challenge: <c>SHA-256("bazaar.enrol.v1" 0x00 nonce humanId deviceId SHA-256(approval key))</c>. One signature ties the
    /// credential to this approval key, device and nonce.
    /// </summary>
    public static byte[] Challenge(byte[] nonce, Guid humanId, Guid deviceId, byte[] approvalPublicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(approvalPublicKeySpki);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(_challengeDomain));
        ms.WriteByte(0);
        ms.Write(nonce);
        ms.Write(humanId.ToByteArray(bigEndian: true));
        ms.Write(deviceId.ToByteArray(bigEndian: true));
        ms.Write(SHA256.HashData(approvalPublicKeySpki));
        return SHA256.HashData(ms.ToArray());
    }

    private static void Append(StringBuilder sb, string value) => sb.Append(value.Length).Append(':').Append(value).Append(';');
}
