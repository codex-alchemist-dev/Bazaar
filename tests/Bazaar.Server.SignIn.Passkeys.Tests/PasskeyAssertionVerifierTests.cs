using System.Security.Cryptography;
using Bazaar.Roles.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.SignIn.Passkeys.Tests;

public class PasskeyAssertionVerifierTests
{
    private const string _rp = "bazaar.example";
    private const string _origin = "https://bazaar.example";

    private sealed class MemoryStore : IPasskeyCredentialStore
    {
        public List<PasskeyCredential> Credentials { get; } = [];

        public Task<PasskeyCredential?> FindAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Credentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(credentialId)));

        public Task<bool> TryAdvanceSignCountAsync(Guid id, long expected, long updated, CancellationToken cancellationToken = default)
        {
            var i = Credentials.FindIndex(c => c.Id == id);
            if (i < 0 || Credentials[i].SignCount != expected)
            {
                return Task.FromResult(false);
            }

            Credentials[i] = Credentials[i] with { SignCount = updated };
            return Task.FromResult(true);
        }
    }

    private sealed record Rig(PasskeyAssertionVerifier Verifier, SoftwareAuthenticator Device, MemoryStore Store, Guid Human, string Challenge);

    private static Rig Build(long signCount = 0)
    {
        var device = new SoftwareAuthenticator();
        var human = Guid.NewGuid();
        var store = new MemoryStore();
        store.Credentials.Add(new PasskeyCredential(Guid.NewGuid(), human, device.CredentialId, device.CosePublicKey, signCount));
        var verifier = new PasskeyAssertionVerifier(store, new PasskeyOptions(_rp, "Bazaar", [_origin]));
        return new Rig(verifier, device, store, human, SoftwareAuthenticator.B64(RandomNumberGenerator.GetBytes(32)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_valid_assertion_signs_in_the_registered_person_and_advances_the_counter()
    {
        var r = Build();

        var identity = await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 5), Ct);

        Assert.NotNull(identity);
        Assert.Equal(r.Human, identity.PrincipalId);
        Assert.Equal(PrincipalKind.Human, identity.Kind);
        Assert.Equal(5, r.Store.Credentials[0].SignCount);
    }

    [Fact]
    public async Task A_replayed_counter_is_refused()
    {
        var r = Build();
        Assert.NotNull(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 5), Ct));

        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 5), Ct));
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 3), Ct));
        Assert.NotNull(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 6), Ct));
    }

    [Fact]
    public async Task An_authenticator_without_a_counter_keeps_working()
    {
        var r = Build();
        Assert.NotNull(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 0), Ct));
        Assert.NotNull(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 0), Ct));
    }

    [Fact]
    public async Task A_wrong_challenge_is_refused()
    {
        var r = Build();
        var other = SoftwareAuthenticator.B64(RandomNumberGenerator.GetBytes(32));
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, other, 1), Ct));
    }

    [Fact]
    public async Task A_wrong_origin_is_refused()
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, "https://evil.example", r.Challenge, 1), Ct));
    }

    [Fact]
    public async Task A_wrong_relying_party_is_refused()
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert("evil.example", _origin, r.Challenge, 1), Ct));
    }

    [Fact]
    public async Task Missing_user_verification_is_refused()
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 1, flags: 0x01), Ct));
    }

    [Fact]
    public async Task A_create_ceremony_type_is_refused()
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 1, type: "webauthn.create"), Ct));
    }

    [Fact]
    public async Task A_bad_signature_is_refused()
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 1, tamper: true), Ct));
    }

    [Fact]
    public async Task An_unknown_credential_is_refused()
    {
        var r = Build();
        var stranger = new SoftwareAuthenticator();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, stranger.Assert(_rp, _origin, r.Challenge, 1), Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"rawId\":\"AA\",\"response\":{}}")]
    public async Task Hostile_input_answers_null(string assertion)
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, assertion, Ct));
        Assert.Null(await r.Verifier.VerifyAsync(string.Empty, r.Device.Assert(_rp, _origin, r.Challenge, 1), Ct));
    }

    [Fact]
    public async Task An_oversized_assertion_is_refused()
    {
        var r = Build();
        Assert.Null(await r.Verifier.VerifyAsync(r.Challenge, new string('a', 20_000), Ct));
    }

    [Fact]
    public async Task A_lost_counter_race_is_refused()
    {
        var r = Build();
        var racing = new RacingStore(r.Store);
        var verifier = new PasskeyAssertionVerifier(racing, new PasskeyOptions(_rp, "Bazaar", [_origin]));
        Assert.Null(await verifier.VerifyAsync(r.Challenge, r.Device.Assert(_rp, _origin, r.Challenge, 7), Ct));
    }

    private sealed class RacingStore(MemoryStore inner) : IPasskeyCredentialStore
    {
        public Task<PasskeyCredential?> FindAsync(byte[] credentialId, CancellationToken cancellationToken = default) => inner.FindAsync(credentialId, cancellationToken);

        public Task<bool> TryAdvanceSignCountAsync(Guid id, long expected, long updated, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Fact]
    public void Options_need_an_rp_id_and_default_the_origin()
    {
        var none = new ConfigurationBuilder().Build();
        Assert.Null(PasskeyOptions.Read(none));

        var set = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["signin:rpId"] = "a.example" }).Build();
        Assert.Equal(["https://a.example"], PasskeyOptions.Read(set)!.Origins);
    }
}
