using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Bazaar.Server.SignIn.Passkeys;

/// <summary>
/// The real <see cref="IPasskeyAssertionVerifier"/> (doc 32 section 4), built on the maintained <c>Fido2</c> library. The library checks
/// the challenge, origin, RP id hash, user presence, signature and the counter; this adapter adds the rules the doc sets: user
/// verification is required, the credential must be registered to an active person, and the signature counter moves only by
/// compare and set so a cloned credential cannot win a race. Any failure, thrown or not, answers null, the same as an unknown
/// credential, so the page gives one generic refusal. Nothing here logs the assertion.
/// </summary>
public sealed class PasskeyAssertionVerifier : IPasskeyAssertionVerifier
{
    private const int _maxAssertionChars = 16 * 1024;

    private readonly IPasskeyCredentialStore _store;
    private readonly Fido2Configuration _config;
    private readonly Fido2 _fido;

    /// <summary>Creates the verifier.</summary>
    public PasskeyAssertionVerifier(IPasskeyCredentialStore store, PasskeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _config = new Fido2Configuration
        {
            RPID = options.RpId,
            RPName = options.DisplayName,
            Origins = new HashSet<string>(options.Origins, StringComparer.Ordinal),
        };
        _fido = new Fido2(_config);
    }

    /// <inheritdoc />
    public async Task<SignInIdentity?> VerifyAsync(string challenge, string assertionJson, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(challenge) || string.IsNullOrEmpty(assertionJson) || assertionJson.Length > _maxAssertionChars)
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson);
            if (response?.RawId is not { Length: > 0 } rawId || response.Response is null)
            {
                return null;
            }

            var credential = await _store.FindAsync(rawId, ct).ConfigureAwait(false);
            if (credential is null)
            {
                return null;
            }

            var challengeBytes = Base64UrlDecode(challenge);
            var options = AssertionOptions.Create(
                _config,
                challengeBytes,
                [new PublicKeyCredentialDescriptor(credential.CredentialId)],
                UserVerificationRequirement.Required,
                null);

            var result = await _fido.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = options,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = checked((uint)credential.SignCount),
                    IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
                },
                ct).ConfigureAwait(false);

            // A counter that stays at zero is an authenticator with no counter; one that moves must move forward, once.
            var next = (long)result.SignCount;
            if (next != 0 || credential.SignCount != 0)
            {
                if (next <= credential.SignCount || !await _store.TryAdvanceSignCountAsync(credential.Id, credential.SignCount, next, ct).ConfigureAwait(false))
                {
                    return null;
                }
            }

            return new SignInIdentity(credential.HumanId, PrincipalKind.Human, Guid.CreateVersion7());
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string value) => System.Buffers.Text.Base64Url.DecodeFromChars(value);
}
