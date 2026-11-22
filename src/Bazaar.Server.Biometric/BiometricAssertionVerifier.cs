using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Server.Biometric.WebAuthn;

namespace Bazaar.Server.Biometric;

/// <summary>
/// Verifies a <see cref="BiometricAssertion"/> against the action it approves (doc 48 section 8.1). The checks run in order and the
/// first failure refuses: version and sizes; a registered, unrevoked credential of the deciding human; a single-device credential;
/// rpIdHash and the presence and verification flags; client data type, origin and challenge; the ES256 signature; the sign count;
/// the clock; and only then the claimed factor and liveness. The claims are read after the signature, so an unsigned claim is
/// never acted on. Hostile input never throws.
/// </summary>
public sealed class BiometricAssertionVerifier(IBiometricCredentialStore store, BiometricPolicy policy) : IBiometricAssertionVerifier
{
    private const int _maxCredentialIdBytes = 1024;
    private const int _maxSignatureBytes = 512;

    private readonly byte[] _rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(policy.RelyingPartyId));

    public async Task<BiometricVerdict> VerifyAsync(BiometricAssertion assertion, OwnerPayload payload, bool highRisk, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            return await VerifyCoreAsync(assertion, payload, highRisk, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            return BiometricVerdict.Malformed;
        }
    }

    private async Task<BiometricVerdict> VerifyCoreAsync(BiometricAssertion? a, OwnerPayload payload, bool highRisk, DateTimeOffset now, CancellationToken ct)
    {
        if (a is null || a.Version != 1 || a.CredentialId is not { Length: > 0 and <= _maxCredentialIdBytes }
            || a.Signature is not { Length: > 0 and <= _maxSignatureBytes } || a.AuthenticatorData is null || a.ClientDataJson is null)
            return BiometricVerdict.Malformed;

        if (!AuthenticatorData.TryParse(a.AuthenticatorData, out var auth) || !ClientData.TryParse(a.ClientDataJson, out var client) || client is null)
            return BiometricVerdict.Malformed;

        var credential = await store.GetAsync(a.CredentialId, ct).ConfigureAwait(false);
        if (credential is null)
            return BiometricVerdict.UnknownCredential;
        if (credential.Revoked)
            return BiometricVerdict.CredentialRevoked;
        if (credential.HumanId != payload.HumanId || !string.Equals(credential.DeviceId, payload.DeviceId, StringComparison.Ordinal))
            return BiometricVerdict.WrongHuman;
        if (auth.Synced)
            return BiometricVerdict.SyncedCredential;

        if (!CryptographicOperations.FixedTimeEquals(auth.RpIdHash, _rpIdHash))
            return BiometricVerdict.RelyingPartyMismatch;
        if (!auth.UserPresent || !auth.UserVerified)
            return BiometricVerdict.NotUserVerified;

        if (client.Type != "webauthn.get" || client.Origin != "bazaar-device:" + credential.DeviceId)
            return BiometricVerdict.ClientDataMismatch;
        if (!CryptographicOperations.FixedTimeEquals(client.Challenge, BiometricChallenge.For(payload)))
            return BiometricVerdict.ChallengeMismatch;

        if (!SignatureValid(credential.PublicKeySpki, a.AuthenticatorData, a.ClientDataJson, a.Signature))
            return BiometricVerdict.BadSignature;

        var counter = await AdvanceCounterAsync(credential, auth.SignCount, ct).ConfigureAwait(false);
        if (counter != BiometricVerdict.Valid)
            return counter;

        // The claims are signed now, so they can be read.
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeMilliseconds(client.AtMilliseconds)).TotalMilliseconds) > policy.ClockSkew.TotalMilliseconds)
            return BiometricVerdict.Stale;

        if (!BiometricNames.TryParseFactor(client.FactorName, out var factor) || !credential.AttestedFactors.Contains(factor))
            return BiometricVerdict.FactorNotAllowed;
        if (highRisk)
        {
            if (!policy.HighRiskFactors.Contains(factor))
                return BiometricVerdict.FactorNotAllowed;
            if (client.LivenessName != "passed")
                return BiometricVerdict.LivenessMissing;
        }

        return BiometricVerdict.Valid;
    }

    private async Task<BiometricVerdict> AdvanceCounterAsync(BiometricCredential credential, uint presented, CancellationToken ct)
    {
        var stored = credential.SignCount;
        if (stored == 0 && presented == 0)
            return BiometricVerdict.Valid; // an authenticator without a counter; the owner nonce still makes the action single-use

        if (presented <= stored)
        {
            await store.RevokeAsync(credential.CredentialId, ct).ConfigureAwait(false);
            return BiometricVerdict.SignCountReplay;
        }

        return await store.AdvanceSignCountAsync(credential.CredentialId, stored, presented, ct).ConfigureAwait(false)
            ? BiometricVerdict.Valid
            : BiometricVerdict.SignCountReplay;
    }

    private static bool SignatureValid(byte[] spki, byte[] authenticatorData, byte[] clientDataJson, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        if (key.KeySize != 256)
            return false;

        var signed = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signed, 0);
        SHA256.HashData(clientDataJson, signed.AsSpan(authenticatorData.Length));
        return key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
