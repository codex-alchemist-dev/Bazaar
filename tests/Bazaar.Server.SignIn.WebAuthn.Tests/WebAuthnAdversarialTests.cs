using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Bazaar.Server.SignIn.WebAuthn.Registration;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

/// <summary>
/// Security review of the hand-written verifier: tampering with each signed part, malformed signatures, cross-origin framing,
/// and attestation shapes a hostile client could send. The W3C spec publishes no fixed test vectors, so every case here is built
/// from real signatures made by <see cref="TestAuthenticator"/> and then altered one field at a time.
/// </summary>
public sealed class WebAuthnAdversarialTests
{
    private static readonly Guid _principal = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly WebAuthnOptions _options = new() { RpId = TestAuthenticator.RpId, AllowedOrigins = [TestAuthenticator.Origin] };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TestAuthenticator Auth, WebAuthnAssertionVerifier Verifier)> SetUpAsync()
    {
        var auth = new TestAuthenticator();
        var store = new InMemoryPasskeyCredentialStore();
        Assert.True(await store.AddAsync(
            new StoredPasskeyCredential(Guid.NewGuid(), _principal, Guid.NewGuid(), auth.CredentialId, auth.PublicKeySpki, 0, "t", DateTimeOffset.UnixEpoch), Ct));
        return (auth, new WebAuthnAssertionVerifier(_options, store, new FakeTimeProvider()));
    }

    private static string Mutate(string json, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        edit(node["response"]!.AsObject());
        return node.ToJsonString();
    }

    private static string FlipFirstBit(string b64)
    {
        var bytes = Base64Url.DecodeFromChars(b64);
        bytes[0] ^= 0x01;
        return Base64Url.EncodeToString(bytes);
    }

    private static string FlipLastBit(string b64)
    {
        var bytes = Base64Url.DecodeFromChars(b64);
        bytes[^1] ^= 0x01;
        return Base64Url.EncodeToString(bytes);
    }

    [Fact]
    public async Task A_valid_assertion_is_the_baseline_for_these_cases()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.NotNull(await verifier.VerifyAsync(challenge, auth.Assert(challenge, 1), Ct));
    }

    [Theory]
    [InlineData("authenticatorData")]
    [InlineData("clientDataJSON")]
    [InlineData("signature")]
    public async Task Flipping_a_bit_in_any_signed_part_is_refused(string field)
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var tampered = Mutate(auth.Assert(challenge, 1), r => r[field] = field == "signature" ? FlipLastBit(r[field]!.GetValue<string>()) : FlipFirstBit(r[field]!.GetValue<string>()));

        Assert.Null(await verifier.VerifyAsync(challenge, tampered, Ct));
    }

    [Fact]
    public async Task A_signature_with_trailing_bytes_or_a_truncated_one_is_refused()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var good = auth.Assert(challenge, 1);

        var padded = Mutate(good, r => r["signature"] = Base64Url.EncodeToString([.. Base64Url.DecodeFromChars(r["signature"]!.GetValue<string>()), 0x00]));
        var truncated = Mutate(good, r =>
        {
            var sig = Base64Url.DecodeFromChars(r["signature"]!.GetValue<string>());
            r["signature"] = Base64Url.EncodeToString(sig.AsSpan(0, sig.Length - 2));
        });
        var raw64 = Mutate(good, r => r["signature"] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64)));

        Assert.Null(await verifier.VerifyAsync(challenge, padded, Ct));
        Assert.Null(await verifier.VerifyAsync(challenge, truncated, Ct));
        Assert.Null(await verifier.VerifyAsync(challenge, raw64, Ct));
    }

    [Fact]
    public async Task Cross_origin_client_data_is_refused_even_with_a_valid_signature()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var framed = auth.AssertWithClientData(challenge, 1, new { type = "webauthn.get", challenge, origin = TestAuthenticator.Origin, crossOrigin = true });
        var unframed = auth.AssertWithClientData(challenge, 2, new { type = "webauthn.get", challenge, origin = TestAuthenticator.Origin });

        Assert.Null(await verifier.VerifyAsync(challenge, framed, Ct));
        Assert.NotNull(await verifier.VerifyAsync(challenge, unframed, Ct));
    }

    [Fact]
    public async Task Origins_that_only_look_like_the_allowed_one_are_refused()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();

        foreach (var origin in new[]
        {
            "https://bazaar.example.evil.example", "https://evil.bazaar.example", "http://bazaar.example", "https://bazaar.example/",
            "https://bazaar.example:8443", "HTTPS://BAZAAR.EXAMPLE", "https://bazaar.example@evil.example", "",
        })
        {
            Assert.Null(await verifier.VerifyAsync(challenge, auth.Assert(challenge, 1, origin: origin), Ct));
        }
    }

    [Fact]
    public async Task A_user_handle_of_the_wrong_length_is_refused()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await verifier.VerifyAsync(challenge, auth.Assert(challenge, 1, userHandle: new byte[8]), Ct));
        Assert.Null(await verifier.VerifyAsync(challenge, auth.Assert(challenge, 1, userHandle: new byte[40]), Ct));
    }

    [Fact]
    public async Task A_challenge_issued_for_another_login_is_refused_in_any_encoding()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var signed = auth.Assert(challenge, 1);

        Assert.Null(await verifier.VerifyAsync(TestAuthenticator.NewChallenge(), signed, Ct));
        Assert.Null(await verifier.VerifyAsync(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(challenge)), signed, Ct));
        Assert.Null(await verifier.VerifyAsync("AAAA", signed, Ct));
    }

    [Fact]
    public async Task An_envelope_with_the_wrong_type_or_a_mismatched_raw_id_is_refused()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var node = JsonNode.Parse(auth.Assert(challenge, 1))!.AsObject();
        node["type"] = "password";

        Assert.Null(await verifier.VerifyAsync(challenge, node.ToJsonString(), Ct));

        var other = JsonNode.Parse(auth.Assert(challenge, 1))!.AsObject();
        other["rawId"] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        Assert.Null(await verifier.VerifyAsync(challenge, other.ToJsonString(), Ct));
    }

    [Fact]
    public async Task Short_authenticator_data_is_refused()
    {
        var (auth, verifier) = await SetUpAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await verifier.VerifyAsync(challenge, Mutate(auth.Assert(challenge, 1), r => r["authenticatorData"] = Base64Url.EncodeToString(new byte[36])), Ct));
    }

    [Fact]
    public void An_extensions_flag_or_backup_state_without_eligibility_in_an_attestation_is_refused()
    {
        var verifier = new AttestationVerifier(_options);
        var auth = new TestAuthenticator();
        var challenge = TestAuthenticator.NewChallenge();
        const byte basic = TestAuthenticator.UserPresent | TestAuthenticator.UserVerified | TestAuthenticator.Attested;

        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, flags: basic | 0x80)));
        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, flags: basic | TestAuthenticator.BackupState)));
        Assert.NotNull(verifier.Verify(challenge, auth.Attest(challenge, flags: basic | TestAuthenticator.BackupState | TestAuthenticator.BackupEligible)));
    }

    [Fact]
    public void A_cose_key_with_extra_labels_wrong_curve_or_trailing_bytes_is_refused()
    {
        var verifier = new AttestationVerifier(_options);
        var auth = new TestAuthenticator();
        var challenge = TestAuthenticator.NewChallenge();
        var cose = auth.CoseKey();

        byte[] withExtra = [0xA6, .. cose[1..], 0x04, 0x01];
        byte[] withTrailing = [.. cose, 0x00];
        byte[] wrongCurve = [.. cose];
        wrongCurve[7] = 0x02; // crv label -1 value 1 -> 2 (P-384)
        byte[] wrongKty = [.. cose];
        wrongKty[3] = 0x01; // kty 2 -> 1 (OKP)
        byte[] duplicateX = [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. new byte[32], 0x21, 0x58, 0x20, .. new byte[32]];

        Assert.NotNull(verifier.Verify(challenge, auth.Attest(challenge)));
        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, coseOverride: withExtra)));
        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, coseOverride: withTrailing)));
        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, coseOverride: wrongCurve)));
        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, coseOverride: wrongKty)));
        Assert.Null(verifier.Verify(challenge, auth.Attest(challenge, coseOverride: duplicateX)));
    }

    [Fact]
    public void An_attestation_object_with_a_duplicate_or_extra_key_or_trailing_bytes_is_refused()
    {
        var verifier = new AttestationVerifier(_options);
        var auth = new TestAuthenticator();
        var challenge = TestAuthenticator.NewChallenge();
        var node = JsonNode.Parse(auth.Attest(challenge))!.AsObject();
        var att = Base64Url.DecodeFromChars(node["response"]!["attestationObject"]!.GetValue<string>());

        node["response"]!["attestationObject"] = Base64Url.EncodeToString([.. att, 0x00]);
        Assert.Null(verifier.Verify(challenge, node.ToJsonString()));

        node["response"]!["attestationObject"] = Base64Url.EncodeToString([0xA4, .. att[1..]]);
        Assert.Null(verifier.Verify(challenge, node.ToJsonString()));
    }

    [Fact]
    public async Task A_credential_id_cannot_be_registered_twice_even_after_revocation()
    {
        var store = new InMemoryPasskeyCredentialStore();
        var auth = new TestAuthenticator();
        var first = new StoredPasskeyCredential(Guid.NewGuid(), _principal, Guid.NewGuid(), auth.CredentialId, auth.PublicKeySpki, 0, "a", DateTimeOffset.UnixEpoch);
        var other = first with { Id = Guid.NewGuid(), PrincipalId = Guid.NewGuid() };

        Assert.True(await store.AddAsync(first, Ct));
        Assert.False(await store.AddAsync(other, Ct));
        Assert.True(await store.RevokeAsync(first.Id, DateTimeOffset.UnixEpoch, Ct));
        Assert.False(await store.AddAsync(other, Ct));
    }
}
