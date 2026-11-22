using Bazaar.Approvals;

namespace Bazaar.Server.Biometric.Tests;

public sealed class BiometricAssertionVerifierTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MemoryStore : IBiometricCredentialStore
    {
        public Dictionary<string, BiometricCredential> Credentials { get; } = [];
        public int Revocations { get; private set; }

        public Task<BiometricCredential?> GetAsync(byte[] credentialId, CancellationToken ct = default) =>
            Task.FromResult(Credentials.GetValueOrDefault(Convert.ToHexString(credentialId)));

        public Task<bool> AdvanceSignCountAsync(byte[] credentialId, uint expectedCount, uint newCount, CancellationToken ct = default)
        {
            var key = Convert.ToHexString(credentialId);
            var c = Credentials[key];
            if (c.SignCount != expectedCount)
                return Task.FromResult(false);
            Credentials[key] = c with { SignCount = newCount };
            return Task.FromResult(true);
        }

        public Task RevokeAsync(byte[] credentialId, CancellationToken ct = default)
        {
            var key = Convert.ToHexString(credentialId);
            Credentials[key] = Credentials[key] with { Revoked = true };
            Revocations++;
            return Task.CompletedTask;
        }
    }

    private readonly Guid _human = Guid.NewGuid();
    private readonly SoftwareAuthenticator _device;
    private readonly MemoryStore _store = new();
    private readonly BiometricAssertionVerifier _verifier;

    public BiometricAssertionVerifierTests()
    {
        _device = new SoftwareAuthenticator(_human, "device-1");
        Enrol(_device, [BiometricFactor.FaceDepth, BiometricFactor.FaceMulti, BiometricFactor.Fingerprint, BiometricFactor.Voice, BiometricFactor.HardwareKey]);
        _verifier = new BiometricAssertionVerifier(_store, new BiometricPolicy { RelyingPartyId = SoftwareAuthenticator.RelyingParty });
    }

    public void Dispose() => _device.Dispose();

    private void Enrol(SoftwareAuthenticator d, BiometricFactor[] attested, uint signCount = 0) =>
        _store.Credentials[Convert.ToHexString(d.CredentialId)] =
            new BiometricCredential(d.CredentialId, d.Human, d.DeviceId, d.PublicKeySpki, signCount, attested.ToHashSet(), false);

    private Task<BiometricVerdict> VerifyAsync(BiometricAssertion a, OwnerPayload p, bool highRisk = true) =>
        _verifier.VerifyAsync(a, p, highRisk, DateTimeOffset.UtcNow, Ct);

    [Fact]
    public async Task A_correct_assertion_for_a_high_risk_action_is_valid_and_advances_the_counter()
    {
        var p = _device.Payload();
        Assert.Equal(BiometricVerdict.Valid, await VerifyAsync(_device.Assert(p, signCount: 5), p));
        Assert.Equal(5u, _store.Credentials[Convert.ToHexString(_device.CredentialId)].SignCount);
    }

    [Fact]
    public async Task An_assertion_for_one_action_does_not_pass_for_another()
    {
        var a = _device.Payload();
        var other = _device.Payload("roles.widen");
        Assert.Equal(BiometricVerdict.ChallengeMismatch, await VerifyAsync(_device.Assert(a), other));
    }

    [Fact]
    public async Task A_replayed_or_lower_sign_count_is_refused_and_revokes_the_credential()
    {
        var p = _device.Payload();
        Assert.Equal(BiometricVerdict.Valid, await VerifyAsync(_device.Assert(p, signCount: 7), p));
        var p2 = _device.Payload();
        Assert.Equal(BiometricVerdict.SignCountReplay, await VerifyAsync(_device.Assert(p2, signCount: 7), p2));
        Assert.Equal(1, _store.Revocations);

        var p3 = _device.Payload();
        Assert.Equal(BiometricVerdict.CredentialRevoked, await VerifyAsync(_device.Assert(p3, signCount: 9), p3));
    }

    [Fact]
    public async Task An_authenticator_without_a_counter_is_accepted_repeatedly()
    {
        for (var i = 0; i < 2; i++)
        {
            var p = _device.Payload();
            Assert.Equal(BiometricVerdict.Valid, await VerifyAsync(_device.Assert(p, signCount: 0), p));
        }
    }

    [Fact]
    public async Task A_lost_counter_race_is_refused_without_revoking()
    {
        var p = _device.Payload();
        var racing = new RacingStore(_store);
        var verifier = new BiometricAssertionVerifier(racing, new BiometricPolicy { RelyingPartyId = SoftwareAuthenticator.RelyingParty });
        Assert.Equal(BiometricVerdict.SignCountReplay, await verifier.VerifyAsync(_device.Assert(p, signCount: 3), p, true, DateTimeOffset.UtcNow, Ct));
        Assert.Equal(0, _store.Revocations);
    }

    private sealed class RacingStore(MemoryStore inner) : IBiometricCredentialStore
    {
        public Task<BiometricCredential?> GetAsync(byte[] credentialId, CancellationToken ct = default) => inner.GetAsync(credentialId, ct);

        public Task<bool> AdvanceSignCountAsync(byte[] credentialId, uint expectedCount, uint newCount, CancellationToken ct = default) => Task.FromResult(false);

        public Task RevokeAsync(byte[] credentialId, CancellationToken ct = default) => inner.RevokeAsync(credentialId, ct);
    }

    public static TheoryData<string> Refusals => new() { "version", "origin", "rp", "type", "up", "uv", "synced", "bad-signature", "stale", "future", "factor-not-attested", "factor-not-allowed", "liveness" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Each_hostile_case_is_refused_with_its_reason(string kind)
    {
        var p = _device.Payload();
        (BiometricAssertion Assertion, BiometricVerdict Expected) = kind switch
        {
            "version" => (_device.Assert(p, version: 2), BiometricVerdict.Malformed),
            "origin" => (_device.Assert(p, origin: "https://evil.example"), BiometricVerdict.ClientDataMismatch),
            "rp" => (_device.Assert(p, rp: "evil.example"), BiometricVerdict.RelyingPartyMismatch),
            "type" => (_device.Assert(p, type: "webauthn.create"), BiometricVerdict.ClientDataMismatch),
            "up" => (_device.Assert(p, flags: 0x04), BiometricVerdict.NotUserVerified),
            "uv" => (_device.Assert(p, flags: 0x01), BiometricVerdict.NotUserVerified),
            "synced" => (_device.Assert(p, flags: 0x1D), BiometricVerdict.SyncedCredential),
            "bad-signature" => (Tamper(_device.Assert(p)), BiometricVerdict.BadSignature),
            "stale" => (_device.Assert(p, at: DateTimeOffset.UtcNow.AddMinutes(-5)), BiometricVerdict.Stale),
            "future" => (_device.Assert(p, at: DateTimeOffset.UtcNow.AddMinutes(5)), BiometricVerdict.Stale),
            "factor-not-attested" => (_device.Assert(p, factor: "face-depth"), BiometricVerdict.FactorNotAllowed),
            "factor-not-allowed" => (_device.Assert(p, factor: "voice"), BiometricVerdict.FactorNotAllowed),
            "liveness" => (_device.Assert(p, liveness: "not-reported"), BiometricVerdict.LivenessMissing),
            _ => throw new InvalidOperationException(kind),
        };

        if (kind == "factor-not-attested")
            Enrol(_device, [BiometricFactor.Fingerprint]);

        Assert.Equal(Expected, await VerifyAsync(Assertion, p));
    }

    private static BiometricAssertion Tamper(BiometricAssertion a)
    {
        var sig = a.Signature.ToArray();
        sig[^1] ^= 0xFF;
        return a with { Signature = sig };
    }

    [Fact]
    public async Task Voice_and_hardware_key_pass_a_non_high_risk_check_when_attested()
    {
        var p = _device.Payload();
        Assert.Equal(BiometricVerdict.Valid, await VerifyAsync(_device.Assert(p, factor: "voice", liveness: "not-reported", signCount: 1), p, highRisk: false));
    }

    [Fact]
    public async Task An_unknown_credential_a_revoked_one_and_another_humans_are_refused()
    {
        var p = _device.Payload();
        var stranger = new SoftwareAuthenticator(_human, "device-1");
        Assert.Equal(BiometricVerdict.UnknownCredential, await VerifyAsync(stranger.Assert(p), p));

        var other = _device.Payload();
        var otherHuman = other with { HumanId = Guid.NewGuid() };
        Assert.Equal(BiometricVerdict.WrongHuman, await VerifyAsync(_device.Assert(otherHuman), otherHuman));
        var otherDevice = other with { DeviceId = "device-2" };
        Assert.Equal(BiometricVerdict.WrongHuman, await VerifyAsync(_device.Assert(otherDevice), otherDevice));
    }

    [Fact]
    public async Task A_wrong_key_signature_is_refused()
    {
        var p = _device.Payload();
        using var impostor = new SoftwareAuthenticator(_human, "device-1");
        var forged = impostor.Assert(p) with { CredentialId = _device.CredentialId };
        Assert.Equal(BiometricVerdict.BadSignature, await VerifyAsync(forged, p));
    }

    [Fact]
    public async Task Hostile_input_never_throws()
    {
        var p = _device.Payload();
        var good = _device.Assert(p);
        var cases = new[]
        {
            good with { AuthenticatorData = [] },
            good with { AuthenticatorData = new byte[5000] },
            good with { ClientDataJson = [] },
            good with { ClientDataJson = "{"u8.ToArray() },
            good with { ClientDataJson = new byte[3000] },
            good with { ClientDataJson = "[1,2]"u8.ToArray() },
            good with { ClientDataJson = """{"type":1,"challenge":2,"origin":3}"""u8.ToArray() },
            good with { Signature = [] },
            good with { Signature = new byte[600] },
            good with { CredentialId = [] },
            good with { Signature = [1, 2, 3] },
        };

        foreach (var c in cases)
        {
            var verdict = await VerifyAsync(c, p);
            Assert.NotEqual(BiometricVerdict.Valid, verdict);
        }

        Assert.Equal(BiometricVerdict.Malformed, await _verifier.VerifyAsync(null!, p, true, DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task A_bad_stored_key_is_a_refusal_not_a_crash()
    {
        var p = _device.Payload();
        _store.Credentials[Convert.ToHexString(_device.CredentialId)] = _store.Credentials[Convert.ToHexString(_device.CredentialId)] with { PublicKeySpki = [1, 2, 3] };
        Assert.Equal(BiometricVerdict.Malformed, await VerifyAsync(_device.Assert(p), p));
    }
}
