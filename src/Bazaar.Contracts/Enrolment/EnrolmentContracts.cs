using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Enrolment;

/// <summary>
/// The enrolment wire shapes (docs/design/32 section 5b, PROPOSED). Binary fields travel as standard base64. The human id and the
/// device id are never trusted from the body for identity: the server takes them from the access token, and the body's
/// <see cref="EnrolmentCompleteDto.DeviceId"/> is only checked against the token's device.
/// </summary>
public static class EnrolmentRoutes
{
    /// <summary>POST, no body. Answers <see cref="EnrolmentChallengeDto"/>.</summary>
    public const string Challenge = "/enrol/challenge";

    /// <summary>POST <see cref="EnrolmentCompleteDto"/>. Answers <see cref="EnrolmentReplyDto"/>.</summary>
    public const string Complete = "/enrol/complete";
}

/// <summary>The single-use nonce a device signs into its credential challenge (step 1).</summary>
/// <param name="Nonce">32 random bytes, base64.</param>
/// <param name="ExpiresAt">When the server stops accepting it.</param>
public sealed record EnrolmentChallengeDto(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>The biometric assertion of the approving device, for a further device's enrolment (doc 48 section 8.1).</summary>
public sealed record EnrolmentBiometricDto(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("credentialId")] string CredentialId,
    [property: JsonPropertyName("authenticatorData")] string AuthenticatorData,
    [property: JsonPropertyName("clientDataJson")] string ClientDataJson,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>The owner-signed <c>device.enrol</c> approval from an already enrolled device, with its biometric proof.</summary>
public sealed record EnrolmentApprovalDto(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("approvalId")] Guid ApprovalId,
    [property: JsonPropertyName("argsHash")] string ArgsHash,
    [property: JsonPropertyName("humanId")] Guid HumanId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("biometric")] EnrolmentBiometricDto Biometric);

/// <summary>
/// The enrolment request (step 3). Exactly one of <paramref name="ConsoleCode"/> (first device) and <paramref name="Approval"/>
/// (further devices) is set.
/// </summary>
/// <param name="DeviceId">The device the client believes it is. Refused when it differs from the signed-in device.</param>
/// <param name="DeviceClass">One of <c>windows</c>, <c>linux</c>, <c>android</c>, <c>terminal</c>, <c>other</c>.</param>
/// <param name="Nonce">The nonce from <see cref="EnrolmentChallengeDto"/>, base64.</param>
/// <param name="ApprovalPublicKey">The approval public key, DER SubjectPublicKeyInfo, base64.</param>
/// <param name="ApprovalKeyAttestation">The approval key's platform attestation, one base64 blob per chain element.</param>
public sealed record EnrolmentCompleteDto(
    [property: JsonPropertyName("deviceId")] Guid DeviceId,
    [property: JsonPropertyName("deviceClass")] string DeviceClass,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("approvalPublicKey")] string ApprovalPublicKey,
    [property: JsonPropertyName("approvalKeyAttestation")] IReadOnlyList<string> ApprovalKeyAttestation,
    [property: JsonPropertyName("credentialId")] string CredentialId,
    [property: JsonPropertyName("credentialAttestation")] string CredentialAttestation,
    [property: JsonPropertyName("credentialClientData")] string CredentialClientData,
    [property: JsonPropertyName("consoleCode")] string? ConsoleCode,
    [property: JsonPropertyName("approval")] EnrolmentApprovalDto? Approval);

/// <summary>The outcome. <paramref name="Reason"/> is a fixed code and never carries key material.</summary>
public sealed record EnrolmentReplyDto(
    [property: JsonPropertyName("enrolled")] bool Enrolled,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("credentialAttested")] bool CredentialAttested);

/// <summary>Source-generated JSON for the enrolment contracts (camelCase, nulls omitted, unknown members skipped).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(EnrolmentChallengeDto))]
[JsonSerializable(typeof(EnrolmentCompleteDto))]
[JsonSerializable(typeof(EnrolmentReplyDto))]
public sealed partial class EnrolmentContractsJsonContext : JsonSerializerContext;

/// <summary>The credential challenge both sides compute (doc 32 section 5b step 2).</summary>
public static class EnrolmentChallengeFormula
{
    private const string _domain = "bazaar.enrol.v1";

    /// <summary><c>SHA-256("bazaar.enrol.v1" 0x00 nonce humanId deviceId SHA-256(approval key SPKI))</c>, ids as 16 big-endian bytes.</summary>
    public static byte[] Compute(byte[] nonce, Guid humanId, Guid deviceId, byte[] approvalPublicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(approvalPublicKeySpki);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(_domain));
        ms.WriteByte(0);
        ms.Write(nonce);
        ms.Write(humanId.ToByteArray(bigEndian: true));
        ms.Write(deviceId.ToByteArray(bigEndian: true));
        ms.Write(SHA256.HashData(approvalPublicKeySpki));
        return SHA256.HashData(ms.ToArray());
    }
}
