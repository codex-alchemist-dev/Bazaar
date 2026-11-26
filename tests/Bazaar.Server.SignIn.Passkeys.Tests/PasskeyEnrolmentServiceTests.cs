using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.SignIn.Passkeys.Tests;

public class PasskeyEnrolmentServiceTests
{
    private const string _rp = "bazaar.example";
    private const string _origin = "https://bazaar.example";

    private sealed class Store : IPasskeyCredentialStore
    {
        public List<PasskeyCredential> Credentials { get; } = [];

        public Task<PasskeyCredential?> FindAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Credentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(credentialId)));

        public Task<IReadOnlyList<byte[]>> CredentialIdsAsync(Guid humanId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<byte[]>>(Credentials.Where(c => c.HumanId == humanId).Select(c => c.CredentialId).ToList());

        public Task<bool> AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
        {
            if (Credentials.Any(c => c.CredentialId.SequenceEqual(credential.CredentialId)))
            {
                return Task.FromResult(false);
            }

            Credentials.Add(credential);
            return Task.FromResult(true);
        }

        public Task<bool> TryAdvanceSignCountAsync(Guid id, long expected, long updated, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class Caller(Guid human) : IEnrolmentCaller
    {
        public Task<EnrolmentPerson?> ResolveAsync(string bearerToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(bearerToken == "good" ? new EnrolmentPerson(human, "Dana") : null);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (PasskeyEnrolmentService Service, Store Store, Guid Human, FakeTimeProvider Time) Build()
    {
        var human = Guid.NewGuid();
        var store = new Store();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        return (new PasskeyEnrolmentService(store, new Caller(human), new PasskeyOptions(_rp, "Bazaar", [_origin]), time), store, human, time);
    }

    [Fact]
    public async Task A_signed_in_person_enrols_a_passkey_that_then_signs_in()
    {
        var (service, store, human, _) = Build();
        var device = new SoftwareAuthenticator();

        var options = await service.BeginAsync("good", Ct);
        Assert.NotNull(options);
        Assert.True(await service.CompleteAsync("good", SoftwareAttestation.Create(device, options, _rp, _origin), Ct));

        var saved = Assert.Single(store.Credentials);
        Assert.Equal(human, saved.HumanId);
        Assert.Equal(device.CredentialId, saved.CredentialId);

        // The stored key is the one the sign-in verifier needs.
        var verifier = new PasskeyAssertionVerifier(store, new PasskeyOptions(_rp, "Bazaar", [_origin]));
        var challenge = SoftwareAuthenticator.B64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var identity = await verifier.VerifyAsync(challenge, device.Assert(_rp, _origin, challenge, 1), Ct);
        Assert.Equal(human, identity?.PrincipalId);
    }

    [Fact]
    public async Task A_refused_token_gets_no_options_and_cannot_complete()
    {
        var (service, store, _, _) = Build();
        Assert.Null(await service.BeginAsync("bad", Ct));
        Assert.False(await service.CompleteAsync("bad", "{}", Ct));
        Assert.Empty(store.Credentials);
    }

    [Fact]
    public async Task Completing_without_options_is_refused()
    {
        var (service, store, _, _) = Build();
        var other = await Build().Service.BeginAsync("good", Ct);
        Assert.False(await service.CompleteAsync("good", SoftwareAttestation.Create(new SoftwareAuthenticator(), other!, _rp, _origin), Ct));
        Assert.Empty(store.Credentials);
    }

    [Fact]
    public async Task The_options_are_single_use()
    {
        var (service, store, _, _) = Build();
        var device = new SoftwareAuthenticator();
        var options = await service.BeginAsync("good", Ct);
        var bad = SoftwareAttestation.Create(device, options!, "evil.example", _origin);

        Assert.False(await service.CompleteAsync("good", bad, Ct));
        Assert.False(await service.CompleteAsync("good", SoftwareAttestation.Create(device, options!, _rp, _origin), Ct));
        Assert.Empty(store.Credentials);
    }

    [Fact]
    public async Task Options_expire()
    {
        var (service, store, _, time) = Build();
        var device = new SoftwareAuthenticator();
        var options = await service.BeginAsync("good", Ct);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await service.CompleteAsync("good", SoftwareAttestation.Create(device, options!, _rp, _origin), Ct));
        Assert.Empty(store.Credentials);
    }

    [Theory]
    [InlineData("evil", "https://bazaar.example", "webauthn.create")]
    [InlineData("bazaar.example", "https://evil.example", "webauthn.create")]
    [InlineData("bazaar.example", "https://bazaar.example", "webauthn.get")]
    public async Task A_wrong_rp_origin_or_ceremony_is_refused(string rp, string origin, string type)
    {
        var (service, store, _, _) = Build();
        var options = await service.BeginAsync("good", Ct);
        Assert.False(await service.CompleteAsync("good", SoftwareAttestation.Create(new SoftwareAuthenticator(), options!, rp, origin, type: type), Ct));
        Assert.Empty(store.Credentials);
    }

    [Fact]
    public async Task Missing_user_verification_is_refused()
    {
        var (service, store, _, _) = Build();
        var options = await service.BeginAsync("good", Ct);
        Assert.False(await service.CompleteAsync("good", SoftwareAttestation.Create(new SoftwareAuthenticator(), options!, _rp, _origin, flags: 0x41), Ct));
        Assert.Empty(store.Credentials);
    }

    [Fact]
    public async Task A_person_at_the_limit_gets_no_options()
    {
        var (service, store, human, _) = Build();
        for (var i = 0; i < PasskeyEnrolmentService.MaxPasskeysPerPerson; i++)
        {
            store.Credentials.Add(new PasskeyCredential(Guid.NewGuid(), human, [(byte)i, 1], [1], 0));
        }

        Assert.Null(await service.BeginAsync("good", Ct));
    }

    [Fact]
    public async Task Hostile_and_oversized_attestations_are_refused()
    {
        var (service, _, _, _) = Build();
        await service.BeginAsync("good", Ct);
        Assert.False(await service.CompleteAsync("good", "not json", Ct));
        await service.BeginAsync("good", Ct);
        Assert.False(await service.CompleteAsync("good", new string('a', 40_000), Ct));
    }
}
