using Bazaar.Server.SignIn.WebAuthn.Registration;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

public sealed class AttestationVerifierTests
{
    private static readonly WebAuthnOptions _options = new() { RpId = TestAuthenticator.RpId, AllowedOrigins = [TestAuthenticator.Origin] };
    private readonly AttestationVerifier _verifier = new(_options);
    private readonly TestAuthenticator _auth = new();

    [Fact]
    public void Valid_none_attestation_yields_the_credential_and_the_public_key()
    {
        var challenge = TestAuthenticator.NewChallenge();

        var result = _verifier.Verify(challenge, _auth.Attest(challenge));

        Assert.NotNull(result);
        Assert.Equal(_auth.CredentialId, result.CredentialId);
        Assert.Equal(_auth.PublicKeySpki, result.PublicKey);
        Assert.Equal(0, result.SignCount);
    }

    [Fact]
    public void Wrong_challenge_origin_rp_type_are_refused()
    {
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(_verifier.Verify(TestAuthenticator.NewChallenge(), _auth.Attest(challenge)));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, origin: "https://evil.example")));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, rpId: "evil.example")));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, type: "webauthn.get")));
    }

    [Fact]
    public void Missing_user_verification_or_attested_data_is_refused()
    {
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, flags: TestAuthenticator.UserPresent | TestAuthenticator.Attested)));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, flags: TestAuthenticator.UserVerified | TestAuthenticator.Attested)));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, flags: TestAuthenticator.UserPresent | TestAuthenticator.UserVerified)));
    }

    [Fact]
    public void Only_the_none_format_with_an_empty_statement_is_accepted()
    {
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, fmt: "packed")));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, emptyStatement: false)));
    }

    [Fact]
    public void A_credential_id_that_disagrees_with_the_raw_id_is_refused()
    {
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, credentialIdInAuthData: new byte[32])));
    }

    [Fact]
    public void A_non_es256_key_or_an_off_curve_point_is_refused()
    {
        var challenge = TestAuthenticator.NewChallenge();
        var cose = _auth.CoseKey();
        var offCurve = (byte[])cose.Clone();
        offCurve[^1] ^= 0xFF;
        offCurve[^2] ^= 0xFF;

        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, coseOverride: _auth.CoseKey(algorithm: -257))));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, coseOverride: offCurve)));
        Assert.Null(_verifier.Verify(challenge, _auth.Attest(challenge, coseOverride: [.. cose, 0x00])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("{\"type\":\"public-key\",\"rawId\":\"AAAA\",\"response\":{\"clientDataJSON\":\"AAAA\",\"attestationObject\":\"AAAA\"}}")]
    public void Hostile_input_returns_null_and_never_throws(string json)
    {
        Assert.Null(_verifier.Verify(TestAuthenticator.NewChallenge(), json));
    }

    [Fact]
    public void Unconfigured_options_refuse()
    {
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(new AttestationVerifier(new WebAuthnOptions()).Verify(challenge, _auth.Attest(challenge)));
    }
}
