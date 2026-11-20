using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Server.Tests.Security.Approvals;

/// <summary>The shared owner-signature contract (doc 32 section 5a): payload layout and the verifier's verdicts.</summary>
public sealed class OwnerSignatureVerifierTests
{
    private static readonly Guid _human = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    private static readonly DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static OwnerPayload Payload(
        string action = "pr.merge",
        string? headSha = "0123456789abcdef0123456789abcdef01234567",
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? issuedAt = null,
        byte? nonceSeed = 1) =>
        new(
            action,
            Guid.Parse("0196a6f0-0000-7000-8000-000000000302"),
            SHA256.HashData(Encoding.UTF8.GetBytes("args")),
            headSha,
            _human,
            Signing.DeviceId,
            issuedAt ?? _now.AddMinutes(-1),
            expiresAt ?? _now.AddMinutes(4),
            Enumerable.Repeat(nonceSeed ?? 1, OwnerSignedPayload.NonceLength).ToArray());

    private static byte[] Sign(ECDsa key, OwnerPayload payload) =>
        key.SignData(
            OwnerSignedPayload.Build(payload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static (OwnerSignatureVerifier Verifier, InMemoryOwnerKeyRegistry Registry) Setup(ECDsa key, bool revoked = false, string? ownerId = null, OwnerSignatureOptions? options = null)
    {
        var registry = new InMemoryOwnerKeyRegistry();
        registry.Register(new OwnerDeviceKey(ownerId ?? _human.ToString("D"), Signing.DeviceId, Signing.PublicKey(key), revoked));
        return (new OwnerSignatureVerifier(registry, new InMemoryOwnerNonceStore(), options), registry);
    }

    [Fact]
    public void Build_lays_out_the_domain_and_one_line_per_field()
    {
        var text = Encoding.UTF8.GetString(OwnerSignedPayload.Build(Payload()));
        var lines = text.Split('\n');

        Assert.Equal(10, lines.Length);
        Assert.Equal(OwnerSignedPayload.Domain, lines[0]);
        Assert.Equal("pr.merge", lines[1]);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", lines[4]);
        Assert.Equal(_human.ToString("D"), lines[5]);
        Assert.Equal(Signing.DeviceId, lines[6]);
    }

    [Fact]
    public void Build_uses_a_dash_for_an_action_without_a_head_sha()
    {
        var lines = Encoding.UTF8.GetString(OwnerSignedPayload.Build(Payload(headSha: null))).Split('\n');

        Assert.Equal("-", lines[4]);
    }

    [Fact]
    public void Build_refuses_a_field_that_could_add_a_line()
    {
        Assert.Throws<ArgumentException>(() => OwnerSignedPayload.Build(Payload(action: "pr.merge\nevil.action")));
        Assert.Throws<ArgumentException>(() => OwnerSignedPayload.Build(Payload(headSha: "ABCDEF")));
        Assert.Throws<ArgumentException>(() => OwnerSignedPayload.Build(Payload(nonceSeed: null) with { Nonce = new byte[8] }));
    }

    [Fact]
    public async Task A_valid_signature_is_accepted_once()
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key);
        var payload = Payload();
        var signature = Sign(key, payload);

        Assert.Equal(OwnerSignatureVerdict.Valid, await verifier.VerifyAsync(payload, signature, _now, TestContext.Current.CancellationToken));
        Assert.Equal(OwnerSignatureVerdict.NonceReplayed, await verifier.VerifyAsync(payload, signature, _now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_signature_over_a_different_head_is_refused()
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key);
        var signed = Payload(headSha: "0123456789abcdef0123456789abcdef01234567");
        var presented = Payload(headSha: "fedcba9876543210fedcba9876543210fedcba98");

        var verdict = await verifier.VerifyAsync(presented, Sign(key, signed), _now, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.BadSignature, verdict);
    }

    [Fact]
    public async Task A_refused_forgery_does_not_burn_the_nonce()
    {
        using var key = Signing.NewKey();
        using var attacker = Signing.NewKey();
        var (verifier, _) = Setup(key);
        var payload = Payload();

        var forged = await verifier.VerifyAsync(payload, Sign(attacker, payload), _now, TestContext.Current.CancellationToken);
        var genuine = await verifier.VerifyAsync(payload, Sign(key, payload), _now, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.BadSignature, forged);
        Assert.Equal(OwnerSignatureVerdict.Valid, genuine);
    }

    [Fact]
    public async Task An_unknown_device_is_refused()
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key);
        var payload = Payload() with { DeviceId = "other-device" };

        var verdict = await verifier.VerifyAsync(payload, Sign(key, payload), _now, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.UnknownDevice, verdict);
    }

    [Fact]
    public async Task A_revoked_device_is_refused()
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key, revoked: true);
        var payload = Payload();

        var verdict = await verifier.VerifyAsync(payload, Sign(key, payload), _now, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.RevokedDevice, verdict);
    }

    [Fact]
    public async Task A_key_held_by_another_human_is_refused()
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key, ownerId: Guid.Parse("0196a6f0-0000-7000-8000-000000000399").ToString("D"));
        var payload = Payload();

        var verdict = await verifier.VerifyAsync(payload, Sign(key, payload), _now, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.NotTheOwner, verdict);
    }

    [Fact]
    public async Task An_expired_payload_is_refused_before_the_key_is_read()
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key);
        var payload = Payload(expiresAt: _now.AddSeconds(-1));

        var verdict = await verifier.VerifyAsync(payload, Sign(key, payload), _now, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.Expired, verdict);
    }

    private static async Task<OwnerSignatureVerdict> VerdictAsync(OwnerPayload payload, OwnerSignatureOptions? options = null)
    {
        using var key = Signing.NewKey();
        var (verifier, _) = Setup(key, options: options);
        return await verifier.VerifyAsync(payload, Sign(key, payload), _now, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_lifetime_of_exactly_the_maximum_is_accepted()
    {
        var issued = _now.AddMinutes(-1);
        Assert.Equal(OwnerSignatureVerdict.Valid, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(5))));
    }

    [Fact]
    public async Task A_lifetime_one_second_over_the_maximum_is_refused()
    {
        var issued = _now.AddMinutes(-1);
        Assert.Equal(OwnerSignatureVerdict.InvalidLifetime, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(5).AddSeconds(1))));
    }

    [Fact]
    public async Task A_payload_valid_for_years_is_refused()
    {
        Assert.Equal(OwnerSignatureVerdict.InvalidLifetime, await VerdictAsync(Payload(expiresAt: _now.AddDays(900))));
    }

    [Fact]
    public async Task An_expiry_not_after_the_issue_time_is_refused()
    {
        Assert.Equal(OwnerSignatureVerdict.InvalidLifetime, await VerdictAsync(Payload(issuedAt: _now.AddSeconds(30), expiresAt: _now.AddSeconds(10))));
    }

    [Fact]
    public async Task An_issue_time_within_the_skew_is_accepted()
    {
        var issued = _now.AddSeconds(60);
        Assert.Equal(OwnerSignatureVerdict.Valid, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(1))));
    }

    [Fact]
    public async Task An_issue_time_beyond_the_skew_is_refused()
    {
        var issued = _now.AddSeconds(61);
        Assert.Equal(OwnerSignatureVerdict.IssuedInFuture, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(1))));
    }

    [Fact]
    public async Task An_operator_can_tighten_the_lifetime()
    {
        var options = new OwnerSignatureOptions { MaxLifetime = TimeSpan.FromMinutes(1) };
        var issued = _now.AddSeconds(-10);

        Assert.Equal(OwnerSignatureVerdict.Valid, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(1)), options));
        Assert.Equal(OwnerSignatureVerdict.InvalidLifetime, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(1).AddSeconds(1)), options));
    }

    [Fact]
    public async Task An_operator_cannot_loosen_past_the_ceiling()
    {
        var options = new OwnerSignatureOptions { MaxLifetime = TimeSpan.FromDays(30), ClockSkew = TimeSpan.FromHours(1) };
        var issued = _now.AddMinutes(-1);

        Assert.Equal(OwnerSignatureOptions.MaxLifetimeCeiling, options.MaxLifetime);
        Assert.Equal(OwnerSignatureOptions.ClockSkewCeiling, options.ClockSkew);
        Assert.Equal(OwnerSignatureVerdict.Valid, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(15)), options));
        Assert.Equal(OwnerSignatureVerdict.InvalidLifetime, await VerdictAsync(Payload(issuedAt: issued, expiresAt: issued.AddMinutes(15).AddSeconds(1)), options));
        Assert.Equal(OwnerSignatureVerdict.IssuedInFuture, await VerdictAsync(Payload(issuedAt: _now.AddSeconds(61), expiresAt: _now.AddMinutes(2)), options));
    }

    [Fact]
    public void Options_below_the_floor_are_raised()
    {
        var options = new OwnerSignatureOptions { MaxLifetime = TimeSpan.Zero, ClockSkew = TimeSpan.FromSeconds(-5) };

        Assert.Equal(TimeSpan.FromSeconds(1), options.MaxLifetime);
        Assert.Equal(TimeSpan.Zero, options.ClockSkew);
    }
}
