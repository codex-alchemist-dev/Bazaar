using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;

namespace Bazaar.Server.Biometric;

/// <summary>The biometric factor a device claims it used (doc 48 section 8.1). The claim counts only for an attested credential.</summary>
public enum BiometricFactor
{
    FaceDepth,
    FaceMulti,
    Voice,
    Fingerprint,
    HardwareKey,
}

/// <summary>Wire names of <see cref="BiometricFactor"/> and <see cref="BiometricLiveness"/> in the signed client data.</summary>
public static class BiometricNames
{
    public static string Name(BiometricFactor factor) => factor switch
    {
        BiometricFactor.FaceDepth => "face-depth",
        BiometricFactor.FaceMulti => "face-multi",
        BiometricFactor.Voice => "voice",
        BiometricFactor.Fingerprint => "fingerprint",
        BiometricFactor.HardwareKey => "hardware-key",
        _ => throw new ArgumentOutOfRangeException(nameof(factor)),
    };

    public static bool TryParseFactor(string? value, out BiometricFactor factor)
    {
        foreach (var f in Enum.GetValues<BiometricFactor>())
        {
            if (string.Equals(Name(f), value, StringComparison.Ordinal))
            {
                factor = f;
                return true;
            }
        }

        factor = default;
        return false;
    }
}

/// <summary>What the platform reported about liveness for this check.</summary>
public enum BiometricLiveness
{
    Passed,
    NotReported,
}

/// <summary>One assertion as it arrives (doc 48 section 8.1). Every field is the raw bytes the device signed or sent.</summary>
public sealed record BiometricAssertion(int Version, byte[] CredentialId, byte[] AuthenticatorData, byte[] ClientDataJson, byte[] Signature);

/// <summary>
/// A platform credential registered at enrolment. <paramref name="PublicKeySpki"/> is the ES256 public key as a DER
/// SubjectPublicKeyInfo. <paramref name="AttestedFactors"/> is what the verified attestation allows the device to claim; empty
/// means unattested, and an unattested credential never passes.
/// </summary>
public sealed record BiometricCredential(
    byte[] CredentialId,
    Guid HumanId,
    string DeviceId,
    byte[] PublicKeySpki,
    uint SignCount,
    IReadOnlySet<BiometricFactor> AttestedFactors,
    bool Revoked);

/// <summary>Credential storage for the verifier. Enrolment (which verifies the attestation) writes to it; the verifier reads and advances the counter.</summary>
public interface IBiometricCredentialStore
{
    Task<BiometricCredential?> GetAsync(byte[] credentialId, CancellationToken ct = default);

    /// <summary>
    /// Sets the sign count to <paramref name="newCount"/> only while it is still <paramref name="expectedCount"/>. Returns false when
    /// another assertion advanced it first, so two racing uses of one assertion cannot both pass.
    /// </summary>
    Task<bool> AdvanceSignCountAsync(byte[] credentialId, uint expectedCount, uint newCount, CancellationToken ct = default);

    /// <summary>Revokes a credential whose counter went backwards (a suspected clone). It stays revoked until the owner re-enrols.</summary>
    Task RevokeAsync(byte[] credentialId, CancellationToken ct = default);
}

/// <summary>Settings of the verifier (doc 48 section 8.1). Defaults are the shipped ones.</summary>
public sealed class BiometricPolicy
{
    /// <summary>The relying party id the authenticator hashes into its data. Required.</summary>
    public required string RelyingPartyId { get; init; }

    /// <summary>Largest accepted difference between <c>bazaar.at</c> and server time (matches the owner-signature clock skew).</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Factors accepted for high-risk actions (<c>biometric.allowed_factors</c>). Liveness <c>passed</c> is also required there, whatever is set.</summary>
    public IReadOnlySet<BiometricFactor> HighRiskFactors { get; init; } =
        new HashSet<BiometricFactor> { BiometricFactor.FaceDepth, BiometricFactor.FaceMulti, BiometricFactor.Fingerprint };
}

/// <summary>Why an assertion was accepted or refused. Only <see cref="Valid"/> lets an action go on. Reasons carry no key material.</summary>
public enum BiometricVerdict
{
    Valid,
    Malformed,
    UnknownCredential,
    CredentialRevoked,
    WrongHuman,
    SyncedCredential,
    RelyingPartyMismatch,
    NotUserVerified,
    ClientDataMismatch,
    ChallengeMismatch,
    BadSignature,
    SignCountReplay,
    Stale,
    FactorNotAllowed,
    LivenessMissing,
}

/// <summary>The challenge a device must sign for an action: the owner payload is the same one the owner signature covers.</summary>
public static class BiometricChallenge
{
    private const string _domain = "bazaar.biometric.v1";

    /// <summary>SHA-256 over the domain tag, a zero byte and SHA-256 of the owner payload's signed bytes.</summary>
    public static byte[] For(OwnerPayload payload)
    {
        var digest = SHA256.HashData(OwnerSignedPayload.Build(payload));
        var buf = new byte[_domain.Length + 1 + digest.Length];
        Encoding.ASCII.GetBytes(_domain, buf);
        digest.CopyTo(buf, _domain.Length + 1);
        return SHA256.HashData(buf);
    }
}

/// <summary>Verifies one assertion against the action it is meant to approve (doc 48 section 8.1). Hostile input never throws; it returns a refusal.</summary>
public interface IBiometricAssertionVerifier
{
    /// <summary>Runs every check in order. <paramref name="highRisk"/> applies the factor allow-list and the liveness floor.</summary>
    Task<BiometricVerdict> VerifyAsync(BiometricAssertion assertion, OwnerPayload payload, bool highRisk, DateTimeOffset now, CancellationToken ct = default);
}
