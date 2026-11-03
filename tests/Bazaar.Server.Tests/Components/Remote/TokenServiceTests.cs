using System.Text.Json.Nodes;
using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Tests.Components.PrivacyAudit;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Components.Remote;

/// <summary>Records each access token request; the real issuer lives in the identity service (doc 14).</summary>
internal sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
{
    public List<(Guid DeviceId, Guid HumanId, DateTimeOffset ExpiresAt)> Issued { get; } = [];

    public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt)
    {
        Issued.Add((deviceId, humanId, expiresAt));
        return "access-" + Issued.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Rotation, reuse detection, expiry and revocation of refresh credentials over the in-memory store and a fake clock.</summary>
public sealed class TokenServiceTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly PrincipalRef _owner = new(Guid.Parse("0197a000-0000-7000-8000-00000000a001"), "human");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(
        TokenService Service,
        InMemoryRefreshCredentialStore Store,
        FakeAuditLog Log,
        FakeTimeProvider Time,
        FakeAccessTokenIssuer Issuer,
        Guid DeviceId,
        Guid HumanId,
        string Secret);

    /// <summary>A device with one live credential, issued at the start time. The secret is generated at run time.</summary>
    private static async Task<Harness> CreateAsync(RemoteTokenOptions? options = null)
    {
        var time = new FakeTimeProvider(_start);
        var store = new InMemoryRefreshCredentialStore();
        var log = new FakeAuditLog();
        var issuer = new FakeAccessTokenIssuer();
        var service = new TokenService(
            store,
            issuer,
            new PrivacyAuditRecorder(log, time),
            Options.Create(options ?? new RemoteTokenOptions()),
            time);

        var deviceId = Guid.CreateVersion7(_start);
        var humanId = Guid.CreateVersion7(_start);
        var secret = RefreshCredentialSecret.Generate();
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "test device", "linux", Revision: 1, RevokedAt: null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(secret),
            _start,
            _start.AddDays(30),
            Ct);

        return new Harness(service, store, log, time, issuer, deviceId, humanId, secret);
    }

    private static DeviceInfo DeviceOf(Guid deviceId, string deviceClass = "linux") =>
        new(deviceId.ToString("D"), deviceClass, "test-build");

    private static JsonObject PayloadOf(AuditEntry entry) => JsonNode.Parse(entry.Payload)!.AsObject();

    [Fact]
    public async Task Rotation_returns_a_new_256_bit_secret_and_retires_the_old_one()
    {
        var h = await CreateAsync();

        var pair = await h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct);

        Assert.NotEqual(h.Secret, pair.RefreshToken);
        Assert.Equal(43, pair.RefreshToken.Length); // 32 random bytes, base64url without padding
        Assert.Equal("access-1", pair.AccessToken);

        var retired = await h.Store.FindByHashAsync(RefreshCredentialSecret.Hash(h.Secret), Ct);
        Assert.NotNull(retired);
        Assert.Equal(_start, retired.RetiredAt);

        var live = await h.Store.FindByHashAsync(RefreshCredentialSecret.Hash(pair.RefreshToken), Ct);
        Assert.NotNull(live);
        Assert.Null(live.RetiredAt);
        Assert.Equal(pair.RefreshTokenExpiresAt, live.ExpiresAt);
        Assert.Equal(h.DeviceId, live.DeviceId);

        // A normal rotation is not an audited event.
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task Reuse_of_a_retired_secret_revokes_the_device_audits_it_and_issues_no_tokens()
    {
        var h = await CreateAsync();
        await h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct);

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct));

        Assert.Equal(TokenRefreshFailure.ReuseDetected, ex.Failure);
        Assert.Single(h.Issuer.Issued); // only the first, legitimate refresh issued a token

        var device = await h.Store.FindDeviceAsync(h.DeviceId, Ct);
        Assert.NotNull(device);
        Assert.NotNull(device.RevokedAt);
        Assert.Equal(2, device.Revision);

        Assert.Collection(
            h.Log.Entries,
            revoke =>
            {
                Assert.Equal(PrivacyAuditKinds.DeviceRevoked, revoke.Kind);
                Assert.Equal("device:" + h.DeviceId.ToString("D"), revoke.Actor);
                Assert.Equal("refresh_reuse", PayloadOf(revoke)["reason"]!.GetValue<string>());
            },
            reuse =>
            {
                Assert.Equal(PrivacyAuditKinds.TokenReuseDetected, reuse.Kind);
                Assert.Equal(h.DeviceId.ToString("D"), PayloadOf(reuse)["device_id"]!.GetValue<string>());
            });
    }

    [Fact]
    public async Task After_reuse_the_credential_issued_by_the_last_rotation_is_refused()
    {
        var h = await CreateAsync();
        var first = await h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct);
        await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct));

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(first.RefreshToken, DeviceOf(h.DeviceId), Ct));

        Assert.Equal(TokenRefreshFailure.DeviceRevoked, ex.Failure);
        Assert.Single(h.Issuer.Issued);
    }

    [Fact]
    public async Task Unknown_secret_is_rejected_without_tokens_or_audit()
    {
        var h = await CreateAsync();
        var unknown = RefreshCredentialSecret.Generate();

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(unknown, DeviceOf(h.DeviceId), Ct));

        Assert.Equal(TokenRefreshFailure.UnknownCredential, ex.Failure);
        Assert.Empty(h.Issuer.Issued);
        Assert.Empty(h.Log.Entries);
        Assert.Null((await h.Store.FindDeviceAsync(h.DeviceId, Ct))!.RevokedAt);
    }

    [Fact]
    public async Task Blank_secret_is_rejected_as_unknown()
    {
        var h = await CreateAsync();

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync("  ", DeviceOf(h.DeviceId), Ct));

        Assert.Equal(TokenRefreshFailure.UnknownCredential, ex.Failure);
        Assert.Empty(h.Issuer.Issued);
    }

    [Fact]
    public async Task Expired_secret_is_rejected_without_revoking_the_device()
    {
        var h = await CreateAsync();
        h.Time.Advance(TimeSpan.FromDays(30)); // the sliding window of the first credential ends here

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct));

        Assert.Equal(TokenRefreshFailure.ExpiredCredential, ex.Failure);
        Assert.Empty(h.Issuer.Issued);
        Assert.Null((await h.Store.FindDeviceAsync(h.DeviceId, Ct))!.RevokedAt);
    }

    [Fact]
    public async Task Absolute_lifetime_caps_the_sliding_expiry_from_the_first_credential()
    {
        var h = await CreateAsync();
        var secret = h.Secret;
        TokenPair? last = null;

        // Refreshing every 25 days keeps the sliding window open: 55, 80, then the 90-day cap.
        for (var i = 0; i < 3; i++)
        {
            h.Time.Advance(TimeSpan.FromDays(25));
            last = await h.Service.RefreshAsync(secret, DeviceOf(h.DeviceId), Ct);
            secret = last.RefreshToken;
        }

        Assert.Equal(_start.AddDays(90), last!.RefreshTokenExpiresAt);

        h.Time.Advance(TimeSpan.FromDays(15)); // day 90: the absolute lifetime has ended
        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(secret, DeviceOf(h.DeviceId), Ct));
        Assert.Equal(TokenRefreshFailure.ExpiredCredential, ex.Failure);
    }

    [Fact]
    public async Task Revoked_device_cannot_refresh_and_no_credential_is_rotated()
    {
        var h = await CreateAsync();
        await h.Service.RevokeDeviceAsync(h.DeviceId, _owner, Ct);

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct));

        Assert.Equal(TokenRefreshFailure.DeviceRevoked, ex.Failure);
        Assert.Empty(h.Issuer.Issued);
        var credential = await h.Store.FindByHashAsync(RefreshCredentialSecret.Hash(h.Secret), Ct);
        Assert.NotNull(credential);
        Assert.Null(credential.RetiredAt);
    }

    [Fact]
    public async Task RevokeDeviceAsync_sets_revoked_at_bumps_the_revision_and_audits_once()
    {
        var h = await CreateAsync();

        await h.Service.RevokeDeviceAsync(h.DeviceId, _owner, Ct);

        var device = await h.Store.FindDeviceAsync(h.DeviceId, Ct);
        Assert.NotNull(device);
        Assert.Equal(_start, device.RevokedAt);
        Assert.Equal(2, device.Revision);

        var entry = Assert.Single(h.Log.Entries);
        Assert.Equal(PrivacyAuditKinds.DeviceRevoked, entry.Kind);
        Assert.Equal("human:" + _owner.Id.ToString("D"), entry.Actor);
        Assert.Equal("revoke_request", PayloadOf(entry)["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task RevokeDeviceAsync_is_idempotent()
    {
        var h = await CreateAsync();
        await h.Service.RevokeDeviceAsync(h.DeviceId, _owner, Ct);
        var first = await h.Store.FindDeviceAsync(h.DeviceId, Ct);

        h.Time.Advance(TimeSpan.FromHours(1));
        await h.Service.RevokeDeviceAsync(h.DeviceId, _owner, Ct);

        var second = await h.Store.FindDeviceAsync(h.DeviceId, Ct);
        Assert.Equal(first, second); // same revoked_at, same revision
        Assert.Equal(2, second!.Revision);
        Assert.Single(h.Log.Entries);
    }

    [Fact]
    public async Task RevokeDeviceAsync_rejects_an_unknown_device()
    {
        var h = await CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Service.RevokeDeviceAsync(Guid.CreateVersion7(_start), _owner, Ct));

        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task Access_token_lifetime_comes_from_options()
    {
        var h = await CreateAsync(new RemoteTokenOptions { AccessTokenMinutes = 5 });

        var pair = await h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId), Ct);

        Assert.Equal(_start.AddMinutes(5), pair.AccessTokenExpiresAt);
        Assert.Equal(_start.AddMinutes(5), Assert.Single(h.Issuer.Issued).ExpiresAt);
    }

    [Fact]
    public void Options_default_to_the_settings_table_values()
    {
        var defaults = new RemoteTokenOptions();

        Assert.Equal(15, defaults.AccessTokenMinutes);
        Assert.Equal(30, defaults.RefreshSlidingDays);
        Assert.Equal(90, defaults.RefreshAbsoluteDays);
    }

    [Fact]
    public async Task Device_class_that_does_not_match_the_stored_device_is_refused_without_revoking()
    {
        var h = await CreateAsync();

        var ex = await Assert.ThrowsAsync<TokenRefreshException>(() => h.Service.RefreshAsync(h.Secret, DeviceOf(h.DeviceId, "android"), Ct));

        Assert.Equal(TokenRefreshFailure.UnknownCredential, ex.Failure);
        Assert.Empty(h.Issuer.Issued);
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public void Constructor_rejects_an_absolute_lifetime_shorter_than_the_sliding_one()
    {
        var time = new FakeTimeProvider(_start);
        var options = new RemoteTokenOptions { RefreshSlidingDays = 30, RefreshAbsoluteDays = 10 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenService(
            new InMemoryRefreshCredentialStore(),
            new FakeAccessTokenIssuer(),
            new PrivacyAuditRecorder(new FakeAuditLog(), time),
            Options.Create(options),
            time));
    }
}
