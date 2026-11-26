using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.SignIn.WebAuthn;

/// <summary>
/// Verifies WebAuthn assertions from the sign-in page against stored passkeys (doc 32 section 4). Checked, in this order of
/// concern: a configured relying party, the credential id (discoverable credentials: the id picks the key), the client data
/// (type, the challenge the server issued, an allowed origin, not cross-origin), the rp-id hash, user presence, user
/// verification (required), the flag combination, the ES256 signature, the user handle when present, and a strictly increasing
/// sign count with a compare-and-set so a clone or a racing replay cannot both win. A counter that is zero on both sides
/// (an authenticator with no counter, as synced passkeys often are) is accepted. Every failure returns null with no reason, and
/// nothing hostile throws. Real authenticator interoperability is not covered by the unit tests.
/// </summary>
public sealed class WebAuthnAssertionVerifier(
    WebAuthnOptions options,
    IPasskeyCredentialStore credentials,
    TimeProvider time,
    ILogger<WebAuthnAssertionVerifier>? logger = null) : IPasskeyAssertionVerifier
{
    private const int _maxCredentialIdBytes = 1023;
    private const int _maxAuthenticatorDataBytes = 2048;
    private const int _maxSignatureBytes = 512;

    private readonly byte[]? _rpIdHash = options.RpId is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(options.RpId));

    public async Task<SignInIdentity?> VerifyAsync(string challenge, string assertionJson, CancellationToken ct = default)
    {
        try
        {
            return await VerifyCoreAsync(challenge, assertionJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException or OverflowException or InvalidOperationException)
        {
            // The exception text could echo attacker-chosen bytes, so only the type is logged.
            WebAuthnLog.MalformedAssertion(logger ?? NullLogger<WebAuthnAssertionVerifier>.Instance, ex.GetType().Name);
            return null;
        }
    }

    private async Task<SignInIdentity?> VerifyCoreAsync(string challenge, string assertionJson, CancellationToken ct)
    {
        if (_rpIdHash is null || !options.IsConfigured || string.IsNullOrEmpty(challenge)
            || string.IsNullOrEmpty(assertionJson) || assertionJson.Length > WebAuthnWire.MaxEnvelopeChars)
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize(assertionJson, WebAuthnJson.Default.AssertionEnvelope);
        if (envelope?.Response is not { } response || envelope.Type != "public-key")
        {
            return null;
        }

        var rawId = WebAuthnWire.Decode(envelope.RawId, _maxCredentialIdBytes);
        var clientData = WebAuthnWire.Decode(response.ClientDataJSON, WebAuthnWire.MaxClientDataBytes);
        var authData = WebAuthnWire.Decode(response.AuthenticatorData, _maxAuthenticatorDataBytes);
        var signature = WebAuthnWire.Decode(response.Signature, _maxSignatureBytes);
        if (rawId is null || clientData is null || authData is null || signature is null || authData.Length < 37)
        {
            return null;
        }

        var credential = await credentials.FindByCredentialIdAsync(rawId, ct).ConfigureAwait(false);
        if (credential is null || credential.RevokedAt is not null)
        {
            return null;
        }

        if (!WebAuthnWire.ClientDataValid(clientData, "webauthn.get", challenge, options)
            || !CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), _rpIdHash))
        {
            return null;
        }

        var flags = authData[32];
        if ((flags & WebAuthnWire.FlagUserPresent) == 0
            || (flags & WebAuthnWire.FlagUserVerified) == 0
            || ((flags & WebAuthnWire.FlagBackupState) != 0 && (flags & WebAuthnWire.FlagBackupEligible) == 0))
        {
            return null;
        }

        if (!UserHandleMatches(response.UserHandle, credential.PrincipalId))
        {
            return null;
        }

        var signed = new byte[authData.Length + 32];
        authData.CopyTo(signed, 0);
        SHA256.HashData(clientData, signed.AsSpan(authData.Length));
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(credential.PublicKey, out _);
            if (!ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return null;
            }
        }

        var count = (long)BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        if (!await AcceptSignCountAsync(credential, count, ct).ConfigureAwait(false))
        {
            // Not an exception: a rollback means the authenticator was cloned or the response replayed. Ids only, no key material.
            WebAuthnLog.SignCountRollback(logger ?? NullLogger<WebAuthnAssertionVerifier>.Instance, credential.Id);
            return null;
        }

        return new SignInIdentity(credential.PrincipalId, PrincipalKind.Human, credential.DeviceId);
    }

    private async Task<bool> AcceptSignCountAsync(StoredPasskeyCredential credential, long count, CancellationToken ct)
    {
        if (credential.SignCount == 0 && count == 0)
        {
            return true;
        }

        return count > credential.SignCount
            && await credentials.TryAdvanceSignCountAsync(credential.Id, credential.SignCount, count, time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>The user handle is the principal id (set at registration). When the authenticator returns one it must match.</summary>
    private static bool UserHandleMatches(string? userHandle, Guid principalId)
    {
        if (string.IsNullOrEmpty(userHandle))
        {
            return true;
        }

        var bytes = WebAuthnWire.Decode(userHandle, 64);
        return bytes is { Length: 16 } && new Guid(bytes, bigEndian: true) == principalId;
    }
}
