using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Tests.Components.PrivacyAudit;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Components.Remote;

/// <summary>The pairing state machine (doc 32 section 3) over the in-memory stores and a fake clock.</summary>
public sealed class PairingServiceTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _owner = Guid.Parse("0197a000-0000-7000-8000-00000000b001");
    private static readonly Guid _other = Guid.Parse("0197a000-0000-7000-8000-00000000b002");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(
        PairingService Service,
        InMemoryPairingOfferStore Offers,
        InMemoryRefreshCredentialStore Credentials,
        FakeTimeProvider Time);

    private static Harness Create()
    {
        var time = new FakeTimeProvider(_start);
        var offers = new InMemoryPairingOfferStore();
        var credentials = new InMemoryRefreshCredentialStore();
        var service = new PairingService(
            offers,
            credentials,
            new FakeAccessTokenIssuer(),
            new PrivacyAuditRecorder(new FakeAuditLog(), time),
            Options.Create(new RemoteTokenOptions()),
            Options.Create(new PairingOptions()),
            time);
        return new Harness(service, offers, credentials, time);
    }

    [Fact]
    public async Task An_approved_offer_pairs_the_device_once_and_the_code_is_then_spent()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        var auth = await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "Dom's laptop", "windows", Ct);
        await h.Service.ApproveAsync(offer.OfferId, _owner, Ct);

        var poll = await h.Service.PollAsync(auth.DeviceCode, Ct);

        Assert.Equal(PairingPollStatus.Granted, poll.Status);
        Assert.NotNull(poll.Tokens);
        Assert.StartsWith("access-", poll.Tokens.AccessToken, StringComparison.Ordinal);
        var device = await h.Credentials.FindDeviceAsync(poll.DeviceId!.Value, Ct);
        Assert.Equal(_owner, device!.HumanId);
        Assert.Equal("windows", device.Class);
        Assert.NotNull(await h.Credentials.FindByHashAsync(RefreshCredentialSecret.Hash(poll.Tokens.RefreshToken), Ct));

        var again = await Assert.ThrowsAsync<PairingPollException>(() => h.Service.PollAsync(auth.DeviceCode, Ct));
        Assert.Equal("invalid_grant", again.Error);
    }

    [Fact]
    public async Task A_typed_code_is_forgiven_for_case_dashes_and_look_alikes()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "coworker", Ct);
        var typed = offer.UserCode.ToLowerInvariant().Insert(4, "-").Replace('0', 'o');

        var auth = await h.Service.StartDeviceAuthorizationAsync(typed, "phone", "android", Ct);

        Assert.Equal(offer.OfferId, auth.OfferId);
        Assert.Equal(5, auth.IntervalSeconds);
    }

    [Fact]
    public async Task Only_the_human_who_created_the_offer_can_approve_or_deny_it()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);

        var refused = await Assert.ThrowsAsync<PairingRefusedException>(() => h.Service.ApproveAsync(offer.OfferId, _other, Ct));
        Assert.Equal("not_creator", refused.Code);
        var denied = await Assert.ThrowsAsync<PairingRefusedException>(() => h.Service.DenyAsync(offer.OfferId, _other, Ct));
        Assert.Equal("not_creator", denied.Code);
    }

    [Fact]
    public async Task An_unknown_code_and_a_second_start_are_both_refused()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        var unknown = await Assert.ThrowsAsync<PairingRefusedException>(() =>
            h.Service.StartDeviceAuthorizationAsync("ZZZZZZZZ", "laptop", "linux", Ct));
        Assert.Equal("unknown_code", unknown.Code);

        await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);
        var second = await Assert.ThrowsAsync<PairingRefusedException>(() =>
            h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "other", "linux", Ct));
        Assert.Equal("already_used", second.Code);
    }

    [Fact]
    public async Task A_device_that_polls_before_the_answer_is_pending_and_too_fast_is_slowed()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        var auth = await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);

        var first = await h.Service.PollAsync(auth.DeviceCode, Ct);
        var tooSoon = await h.Service.PollAsync(auth.DeviceCode, Ct);
        h.Time.Advance(TimeSpan.FromSeconds(auth.IntervalSeconds));
        var onTime = await h.Service.PollAsync(auth.DeviceCode, Ct);

        Assert.Equal(PairingPollStatus.Pending, first.Status);
        Assert.Equal(PairingPollStatus.SlowDown, tooSoon.Status);
        Assert.Equal(PairingPollStatus.Pending, onTime.Status);
    }

    [Fact]
    public async Task A_denied_offer_ends_the_device_with_access_denied()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        var auth = await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);
        await h.Service.DenyAsync(offer.OfferId, _owner, Ct);

        var error = await Assert.ThrowsAsync<PairingPollException>(() => h.Service.PollAsync(auth.DeviceCode, Ct));

        Assert.Equal("access_denied", error.Error);
    }

    [Fact]
    public async Task An_expired_offer_cannot_be_started_and_its_device_gets_expired_token()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        var auth = await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);

        h.Time.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<PairingPollException>(() => h.Service.PollAsync(auth.DeviceCode, Ct));
        Assert.Equal("expired_token", error.Error);
        var refused = await Assert.ThrowsAsync<PairingRefusedException>(() =>
            h.Service.ApproveAsync(offer.OfferId, _owner, Ct));
        Assert.Equal("not_pending", refused.Code);
    }

    [Fact]
    public async Task An_approval_does_not_outlive_the_offer()
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);
        var auth = await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);
        await h.Service.ApproveAsync(offer.OfferId, _owner, Ct);

        h.Time.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<PairingPollException>(() => h.Service.PollAsync(auth.DeviceCode, Ct));
        Assert.Equal("expired_token", error.Error);
    }

    [Theory]
    [InlineData("", "linux")]
    [InlineData("laptop", "mainframe")]
    public async Task A_bad_label_or_class_is_refused_before_anything_is_stored(string label, string deviceClass)
    {
        var h = Create();
        var offer = await h.Service.CreateOfferAsync(_owner, "owner", Ct);

        var refused = await Assert.ThrowsAsync<PairingRefusedException>(() =>
            h.Service.StartDeviceAuthorizationAsync(offer.UserCode, label, deviceClass, Ct));

        Assert.StartsWith("bad_", refused.Code, StringComparison.Ordinal);
        await h.Service.StartDeviceAuthorizationAsync(offer.UserCode, "laptop", "linux", Ct);
    }

    [Fact]
    public async Task An_unknown_role_is_refused()
    {
        var h = Create();

        var refused = await Assert.ThrowsAsync<PairingRefusedException>(() => h.Service.CreateOfferAsync(_owner, "admin", Ct));

        Assert.Equal("unknown_role", refused.Code);
    }
}
