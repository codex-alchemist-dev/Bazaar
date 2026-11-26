using Bazaar.Roles.Abstractions;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

public sealed class WebAuthnAssertionVerifierTests
{
    private static readonly Guid _principal = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebAuthnOptions Options { get; } = new() { RpId = TestAuthenticator.RpId, AllowedOrigins = [TestAuthenticator.Origin] };

    private sealed class Fixture
    {
        public TestAuthenticator Authenticator { get; } = new();

        public InMemoryPasskeyCredentialStore Store { get; } = new();

        public StoredPasskeyCredential Credential { get; private set; } = null!;

        public WebAuthnAssertionVerifier Verifier { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(long signCount = 0, WebAuthnOptions? options = null)
        {
            var f = new Fixture();
            f.Credential = new StoredPasskeyCredential(
                Guid.NewGuid(), _principal, Guid.NewGuid(), f.Authenticator.CredentialId, f.Authenticator.PublicKeySpki, signCount, "test", DateTimeOffset.UnixEpoch);
            Assert.True(await f.Store.AddAsync(f.Credential, Ct));
            f.Verifier = new WebAuthnAssertionVerifier(options ?? Options, f.Store, new FakeTimeProvider());
            return f;
        }
    }

    [Fact]
    public async Task Valid_assertion_returns_the_principal_and_device()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        var identity = await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1), Ct);

        Assert.NotNull(identity);
        Assert.Equal(_principal, identity.PrincipalId);
        Assert.Equal(f.Credential.DeviceId, identity.DeviceId);
        Assert.Equal(PrincipalKind.Human, identity.Kind);
        Assert.Equal(1, (await f.Store.FindByCredentialIdAsync(f.Credential.CredentialId, Ct))!.SignCount);
    }

    [Fact]
    public async Task Matching_user_handle_is_accepted_and_a_foreign_one_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.NotNull(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, userHandle: _principal.ToByteArray(bigEndian: true)), Ct));
        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 2, userHandle: Guid.NewGuid().ToByteArray(bigEndian: true)), Ct));
    }

    [Fact]
    public async Task Wrong_origin_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, origin: "https://evil.example"), Ct));
    }

    [Fact]
    public async Task Wrong_rp_id_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, rpId: "evil.example"), Ct));
    }

    [Fact]
    public async Task An_answer_to_a_different_challenge_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var old = TestAuthenticator.NewChallenge();
        var assertion = f.Authenticator.Assert(old, 1);

        // The server issued a new challenge: the recorded assertion for the old one must not verify.
        Assert.Null(await f.Verifier.VerifyAsync(TestAuthenticator.NewChallenge(), assertion, Ct));
    }

    [Fact]
    public async Task Replaying_the_same_assertion_is_refused_by_the_sign_count()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var assertion = f.Authenticator.Assert(challenge, 5);

        Assert.NotNull(await f.Verifier.VerifyAsync(challenge, assertion, Ct));
        Assert.Null(await f.Verifier.VerifyAsync(challenge, assertion, Ct));
    }

    [Fact]
    public async Task Sign_count_rollback_is_refused_and_the_stored_count_is_kept()
    {
        var f = await Fixture.CreateAsync(signCount: 10);
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 9), Ct));
        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 10), Ct));
        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 0), Ct));
        Assert.Equal(10, (await f.Store.FindByCredentialIdAsync(f.Credential.CredentialId, Ct))!.SignCount);
        Assert.NotNull(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 11), Ct));
    }

    [Fact]
    public async Task A_counterless_authenticator_is_accepted_while_both_counts_are_zero()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.NotNull(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 0), Ct));
        Assert.NotNull(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 0), Ct));
    }

    [Fact]
    public async Task Missing_user_verification_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, flags: TestAuthenticator.UserPresent), Ct));
    }

    [Fact]
    public async Task Missing_user_presence_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, flags: TestAuthenticator.UserVerified), Ct));
    }

    [Fact]
    public async Task Backup_state_without_backup_eligibility_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();
        const byte Bad = TestAuthenticator.UserPresent | TestAuthenticator.UserVerified | TestAuthenticator.BackupState;
        const byte Synced = TestAuthenticator.UserPresent | TestAuthenticator.UserVerified | TestAuthenticator.BackupState | TestAuthenticator.BackupEligible;

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, flags: Bad), Ct));
        Assert.NotNull(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 2, flags: Synced), Ct));
    }

    [Fact]
    public async Task A_signature_from_another_key_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();
        using var other = TestAuthenticator.NewOtherKey();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, signWith: other), Ct));
    }

    [Fact]
    public async Task Wrong_client_data_type_is_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1, type: "webauthn.create"), Ct));
    }

    [Fact]
    public async Task Unknown_or_revoked_credentials_are_refused()
    {
        var f = await Fixture.CreateAsync();
        var challenge = TestAuthenticator.NewChallenge();
        var stranger = new TestAuthenticator();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, stranger.Assert(challenge, 1), Ct));

        Assert.True(await f.Store.RevokeAsync(f.Credential.Id, DateTimeOffset.UnixEpoch, Ct));
        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1), Ct));
    }

    [Fact]
    public async Task Unconfigured_options_refuse_everything()
    {
        var f = await Fixture.CreateAsync(options: new WebAuthnOptions());
        var challenge = TestAuthenticator.NewChallenge();

        Assert.Null(await f.Verifier.VerifyAsync(challenge, f.Authenticator.Assert(challenge, 1), Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"public-key\",\"rawId\":\"@@@\",\"response\":{}}")]
    [InlineData("[1,2,3]")]
    public async Task Hostile_input_returns_null_and_never_throws(string assertion)
    {
        var f = await Fixture.CreateAsync();

        Assert.Null(await f.Verifier.VerifyAsync(TestAuthenticator.NewChallenge(), assertion, Ct));
        Assert.Null(await f.Verifier.VerifyAsync("", assertion, Ct));
    }

    [Fact]
    public async Task An_oversized_envelope_is_refused()
    {
        var f = await Fixture.CreateAsync();

        Assert.Null(await f.Verifier.VerifyAsync(TestAuthenticator.NewChallenge(), new string(' ', WebAuthnWire.MaxEnvelopeChars + 1), Ct));
    }
}
