using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Server.Biometric;

namespace Bazaar.Server.DeviceEnrolment;

/// <summary>Lifetimes of the enrolment secrets (doc 32 section 5b). Operators may only shorten them.</summary>
public sealed class EnrolmentOptions
{
    public const int MaxChallengeMinutes = 5;
    public const int MaxCodeMinutes = 15;

    public TimeSpan ChallengeLifetime { get; init; } = TimeSpan.FromMinutes(MaxChallengeMinutes);

    public TimeSpan CodeLifetime { get; init; } = TimeSpan.FromMinutes(MaxCodeMinutes);

    internal TimeSpan Challenge => Clamp(ChallengeLifetime, TimeSpan.FromMinutes(MaxChallengeMinutes));

    internal TimeSpan Code => Clamp(CodeLifetime, TimeSpan.FromMinutes(MaxCodeMinutes));

    private static TimeSpan Clamp(TimeSpan value, TimeSpan ceiling) =>
        value <= TimeSpan.Zero || value > ceiling ? ceiling : value;
}

/// <summary>The console enrolment code: 10 characters from a 32-letter alphabet, shown once, stored only as a hash.</summary>
public static class ConsoleEnrolmentCode
{
    private const string _alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 10;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = _alphabet[RandomNumberGenerator.GetInt32(_alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>The hash a store keeps. Case and dashes or spaces are ignored; returns null when the shape is wrong.</summary>
    public static byte[]? Hash(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var normal = code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (normal.Length != Length || normal.Any(c => _alphabet.IndexOf(c, StringComparison.Ordinal) < 0))
        {
            return null;
        }

        return SHA256.HashData(Encoding.ASCII.GetBytes("bazaar.enrol.code.v1\0" + normal));
    }
}

/// <summary>The enrolment ceremony of doc 32 section 5b, against the attestation and store ports.</summary>
public sealed class DeviceEnrolment(
    IEnrolmentStore store,
    IApprovalKeyAttestor keyAttestor,
    ICredentialAttestor credentialAttestor,
    IOwnerSignatureVerifier ownerSignature,
    IBiometricAssertionVerifier biometric,
    IEnrolmentAudit audit,
    EnrolmentOptions? options = null) : IDeviceEnrolment
{
    private const int _nonceLength = 32;
    private const int _maxBlob = 16 * 1024;
    private const int _maxChain = 8;
    private readonly EnrolmentOptions _options = options ?? new EnrolmentOptions();

    /// <inheritdoc />
    public async Task<EnrolmentChallenge> BeginAsync(Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(_nonceLength);
        var expires = now + _options.Challenge;
        await store.SaveChallengeAsync(nonce, humanId, deviceId, expires, ct).ConfigureAwait(false);
        return new EnrolmentChallenge(nonce, expires);
    }

    /// <inheritdoc />
    public async Task<string> IssueConsoleCodeAsync(Guid humanId, DateTimeOffset now, CancellationToken ct = default)
    {
        var code = ConsoleEnrolmentCode.Generate();
        await store.SaveCodeAsync(ConsoleEnrolmentCode.Hash(code)!, humanId, now + _options.Code, ct).ConfigureAwait(false);
        return code;
    }

    /// <inheritdoc />
    public async Task<EnrolmentOutcome> CompleteAsync(EnrolmentRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await RunAsync(request, now, ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            await audit.RefusedAsync(request.HumanId, request.DeviceId, outcome.Verdict, now, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<EnrolmentOutcome> RunAsync(EnrolmentRequest r, DateTimeOffset now, CancellationToken ct)
    {
        if (!WellFormed(r))
        {
            return new EnrolmentOutcome(EnrolmentVerdict.Malformed);
        }

        if (!await store.IsChallengeLiveAsync(r.Nonce, r.HumanId, r.DeviceId, now, ct).ConfigureAwait(false))
        {
            return new EnrolmentOutcome(EnrolmentVerdict.ChallengeInvalid);
        }

        var (starterOk, codeHash) = await CheckStarterAsync(r, now, ct).ConfigureAwait(false);
        if (!starterOk)
        {
            return new EnrolmentOutcome(EnrolmentVerdict.StarterRefused);
        }

        if (!await store.DeviceMatchesAsync(r.DeviceId, r.HumanId, r.Class, ct).ConfigureAwait(false))
        {
            return new EnrolmentOutcome(EnrolmentVerdict.DeviceUnknown);
        }

        if (!IsP256(r.ApprovalPublicKeySpki))
        {
            return new EnrolmentOutcome(EnrolmentVerdict.ApprovalKeyShape);
        }

        var key = await keyAttestor.VerifyAsync(r.Class, r.ApprovalPublicKeySpki, r.ApprovalKeyAttestation, ct).ConfigureAwait(false);
        var strong = key.Verified && key.HardwareBacked && (r.Class != DeviceClass.Android || key.UserAuthPerUse);
        if (!strong)
        {
            return new EnrolmentOutcome(EnrolmentVerdict.ApprovalKeyNotHardware);
        }

        var challenge = EnrolmentArgs.Challenge(r.Nonce, r.HumanId, r.DeviceId, r.ApprovalPublicKeySpki);
        var reg = await credentialAttestor.VerifyAsync(r.CredentialId, r.CredentialAttestation, r.CredentialClientData, challenge, ct).ConfigureAwait(false);
        if (!reg.ProofValid || reg.PublicKeySpki is not { Length: > 0 } || !IsP256(reg.PublicKeySpki))
        {
            return new EnrolmentOutcome(EnrolmentVerdict.CredentialRefused);
        }

        if (!reg.SingleDevice)
        {
            return new EnrolmentOutcome(EnrolmentVerdict.CredentialSynced);
        }

        // Without a verified attestation chain the factor claims count for nothing (doc 48 section 8.1).
        var factors = reg.Attested ? reg.AttestedFactors : new HashSet<BiometricFactor>();
        var credential = new BiometricCredential(r.CredentialId, r.HumanId, r.DeviceId.ToString("D"), reg.PublicKeySpki, reg.SignCount, factors, Revoked: false);
        var commit = new EnrolmentCommit(r.HumanId, r.DeviceId, r.Class, r.Nonce, codeHash, r.ApprovalPublicKeySpki, credential, now);
        var result = await store.CommitAsync(commit, ct).ConfigureAwait(false);
        return result switch
        {
            EnrolmentCommitResult.Committed => new EnrolmentOutcome(EnrolmentVerdict.Enrolled, reg.Attested),
            EnrolmentCommitResult.ChallengeSpent => new EnrolmentOutcome(EnrolmentVerdict.ChallengeInvalid),
            EnrolmentCommitResult.CodeSpent => new EnrolmentOutcome(EnrolmentVerdict.StarterRefused),
            EnrolmentCommitResult.AlreadyEnrolled => new EnrolmentOutcome(EnrolmentVerdict.AlreadyEnrolled),
            _ => new EnrolmentOutcome(EnrolmentVerdict.DeviceUnknown),
        };
    }

    private async Task<(bool Ok, byte[]? CodeHash)> CheckStarterAsync(EnrolmentRequest r, DateTimeOffset now, CancellationToken ct)
    {
        switch (r.Starter)
        {
            case ConsoleCodeStarter c:
                var hash = ConsoleEnrolmentCode.Hash(c.Code);
                var live = hash is not null && await store.IsCodeLiveAsync(hash, r.HumanId, now, ct).ConfigureAwait(false);
                return (live, live ? hash : null);

            case EnrolledDeviceStarter e:
                var p = e.Payload;
                if (p is null || e.Signature is null || e.Biometric is null
                    || p.Action != EnrolmentArgs.Action
                    || p.HumanId != r.HumanId
                    || string.Equals(p.DeviceId, r.DeviceId.ToString("D"), StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(p.ArgsHash, EnrolmentArgs.Hash(r.DeviceId, r.ApprovalPublicKeySpki)))
                {
                    return (false, null);
                }

                if (await ownerSignature.VerifyAsync(p, e.Signature, now, ct).ConfigureAwait(false) != OwnerSignatureVerdict.Valid)
                {
                    return (false, null);
                }

                var bio = await biometric.VerifyAsync(e.Biometric, p, highRisk: true, now, ct).ConfigureAwait(false);
                return (bio == BiometricVerdict.Valid, null);

            default:
                return (false, null);
        }
    }

    private static bool WellFormed(EnrolmentRequest r) =>
        r.Nonce is { Length: _nonceLength }
        && r.ApprovalPublicKeySpki is { Length: > 0 and <= _maxBlob }
        && r.ApprovalKeyAttestation is { Count: <= _maxChain } chain && chain.All(c => c is { Length: <= _maxBlob })
        && r.CredentialId is { Length: > 0 and <= 1024 }
        && r.CredentialAttestation is { Length: <= _maxBlob }
        && r.CredentialClientData is { Length: > 0 and <= _maxBlob }
        && r.Starter is not null
        && Enum.IsDefined(r.Class);

    private static bool IsP256(byte[] spki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
