using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.Signing;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Identity.Tests.Signing;

/// <summary>The production access-token signer over an in-memory key store and a fake clock (identity signing key, docs/design/14).</summary>
public sealed class AccessTokenSignerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private const string _scope = AccessTokenSigner.Scope;

    private sealed class Harness
    {
        public required FakeTimeProvider Time { get; init; }
        public required InMemorySigningKeyStore Store { get; init; }
        public required TestKekWrapper Wrapper { get; init; }
        public required AccessTokenSigner Signer { get; init; }
    }

    private static Harness Create(byte[]? kek = null, AccessTokenSigningOptions? options = null, InMemorySigningKeyStore? store = null)
    {
        var time = new FakeTimeProvider(_start);
        var keyStore = store ?? new InMemorySigningKeyStore();
        var wrapper = new TestKekWrapper(kek ?? RandomNumberGenerator.GetBytes(32));
        return new Harness
        {
            Time = time,
            Store = keyStore,
            Wrapper = wrapper,
            Signer = new AccessTokenSigner(keyStore, wrapper, time, options),
        };
    }

    private static async Task<Guid> LiveKidAsync(Harness h) => (await h.Store.GetLiveAsync(_scope, Ct))!.DekId;

    /// <summary>Re-encodes the mac so only the mac check (not the MAC itself) can refuse the token.</summary>
    private static string WithPayload(string token, string payloadJson)
    {
        var parts = token.Split('.');
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        return string.Join('.', parts[0], parts[1], payload, parts[3]);
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task A_token_round_trips_to_its_device_and_human()
    {
        var h = Create();
        var deviceId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var token = await h.Signer.SignAsync(deviceId, humanId, h.Time.GetUtcNow().AddMinutes(15), Ct);
        var claims = await h.Signer.VerifyAsync(token, Ct);

        Assert.Equal(new AccessTokenClaims(deviceId, humanId), claims);
    }

    [Fact]
    public async Task The_token_names_the_live_key_in_its_kid_and_has_four_parts()
    {
        var h = Create();

        var token = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var parts = token.Split('.');

        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal((await LiveKidAsync(h)).ToString("N"), parts[1]);
    }

    [Fact]
    public async Task A_token_with_a_changed_mac_is_refused()
    {
        var h = Create();
        var token = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var parts = token.Split('.');
        var flippedMac = (parts[3][0] == 'A' ? "B" : "A") + parts[3][1..];

        var claims = await h.Signer.VerifyAsync(string.Join('.', parts[0], parts[1], parts[2], flippedMac), Ct);

        Assert.Null(claims);
    }

    [Fact]
    public async Task A_forged_payload_under_the_real_mac_is_refused()
    {
        var h = Create();
        var token = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var forged = WithPayload(token, "{\"sub\":\"00000000-0000-0000-0000-000000000000\",\"hum\":\"00000000-0000-0000-0000-000000000000\",\"exp\":4102444800}");

        Assert.Null(await h.Signer.VerifyAsync(forged, Ct));
    }

    [Fact]
    public async Task A_token_whose_kid_is_not_a_known_key_is_refused()
    {
        var h = Create();
        var token = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var parts = token.Split('.');
        var unknown = Guid.NewGuid().ToString("N");

        Assert.Null(await h.Signer.VerifyAsync(string.Join('.', parts[0], unknown, parts[2], parts[3]), Ct));
    }

    [Fact]
    public async Task A_token_from_another_signing_key_is_refused()
    {
        var h = Create();
        var other = Create();
        var token = await other.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);

        Assert.Null(await h.Signer.VerifyAsync(token, Ct));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var h = Create();
        var token = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(1), Ct);
        h.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(await h.Signer.VerifyAsync(token, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v1.a.b")]
    [InlineData("v2.00000000000000000000000000000000.e30.AAAA")]
    [InlineData("v1.nothex.e30.AAAA")]
    [InlineData("v1.00000000000000000000000000000000.!!!.AAAA")]
    [InlineData("v1.00000000000000000000000000000000.e30.!!!")]
    [InlineData("v1.00000000000000000000000000000000.e30.AAAA.extra")]
    public async Task Malformed_tokens_are_refused(string token)
    {
        var h = Create();
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);

        Assert.Null(await h.Signer.VerifyAsync(token, Ct));
    }

    [Fact]
    public async Task A_kid_that_is_not_in_canonical_lowercase_N_form_is_refused()
    {
        var h = Create();
        var token = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var parts = token.Split('.');

        Assert.Null(await h.Signer.VerifyAsync(string.Join('.', parts[0], parts[1].ToUpperInvariant(), parts[2], parts[3]), Ct));
    }

    [Fact]
    public async Task A_retired_key_verifies_until_its_grace_ends_then_is_refused_and_its_wrap_is_cleared()
    {
        var h = Create();
        var longLived = h.Time.GetUtcNow().AddDays(1);
        var oldToken = await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), longLived, Ct);
        var oldKid = await LiveKidAsync(h);
        var retiredAt = h.Time.GetUtcNow();
        await h.Signer.RotateAsync(Ct);

        h.Time.Advance(AccessTokenSigningOptions_Grace() - TimeSpan.FromSeconds(1));
        Assert.NotNull(await h.Signer.VerifyAsync(oldToken, Ct));

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await h.Signer.VerifyAsync(oldToken, Ct));

        // A later sign clears the expired wrap; the row stays, and its wrap is gone.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var oldRow = (await h.Store.ListAsync(_scope, Ct)).Single(r => r.DekId == oldKid);
        Assert.Null(oldRow.WrappedDek);
        Assert.Equal(retiredAt, oldRow.RetiredAt);
    }

    [Fact]
    public async Task A_retired_key_still_wrapped_within_grace_keeps_its_wrap()
    {
        var h = Create();
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddDays(1), Ct);
        var oldKid = await LiveKidAsync(h);
        await h.Signer.RotateAsync(Ct);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);

        Assert.NotNull((await h.Store.ListAsync(_scope, Ct)).Single(r => r.DekId == oldKid).WrappedDek);
    }

    [Fact]
    public async Task Rotation_on_demand_makes_a_new_live_key_and_retires_the_old_one()
    {
        var h = Create();
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var first = await LiveKidAsync(h);

        await h.Signer.RotateAsync(Ct);

        var second = await LiveKidAsync(h);
        Assert.NotEqual(first, second);
        Assert.Equal(2, (await h.Store.ListAsync(_scope, Ct)).Count);
        Assert.Single(await h.Store.ListAsync(_scope, Ct), r => r.RetiredAt is null);
    }

    [Fact]
    public async Task The_live_key_rotates_by_itself_once_it_is_due()
    {
        var h = Create(options: new AccessTokenSigningOptions { SigningKeyRotationDays = 30 });
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        var first = await LiveKidAsync(h);

        h.Time.Advance(TimeSpan.FromDays(29));
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        Assert.Equal(first, await LiveKidAsync(h));

        h.Time.Advance(TimeSpan.FromDays(1));
        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);
        Assert.NotEqual(first, await LiveKidAsync(h));
    }

    [Fact]
    public async Task A_token_signed_before_rotation_still_verifies_under_the_retired_key_inside_grace()
    {
        var h = Create();
        var deviceId = Guid.NewGuid();
        var humanId = Guid.NewGuid();
        var token = await h.Signer.SignAsync(deviceId, humanId, h.Time.GetUtcNow().AddDays(1), Ct);
        await h.Signer.RotateAsync(Ct);

        var claims = await h.Signer.VerifyAsync(token, Ct);

        Assert.Equal(new AccessTokenClaims(deviceId, humanId), claims);
    }

    [Fact]
    public async Task When_the_key_cannot_be_unwrapped_sign_throws_and_verify_refuses()
    {
        var kek = RandomNumberGenerator.GetBytes(32);
        var store = new InMemorySigningKeyStore();
        var good = Create(kek, store: store);
        var token = await good.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), good.Time.GetUtcNow().AddMinutes(15), Ct);

        var broken = Create(kek, store: store);
        broken.Wrapper.FailUnwrap = true;
        var brokenSigner = new AccessTokenSigner(store, broken.Wrapper, good.Time);

        Assert.Null(await brokenSigner.VerifyAsync(token, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => brokenSigner.SignAsync(Guid.NewGuid(), Guid.NewGuid(), good.Time.GetUtcNow().AddMinutes(15), Ct).AsTask());
    }

    [Fact]
    public async Task A_key_under_another_kek_is_refused_fail_closed()
    {
        var store = new InMemorySigningKeyStore();
        var good = Create(RandomNumberGenerator.GetBytes(32), store: store);
        var token = await good.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), good.Time.GetUtcNow().AddMinutes(15), Ct);

        var otherKek = new AccessTokenSigner(store, new TestKekWrapper(RandomNumberGenerator.GetBytes(32)), good.Time);

        Assert.Null(await otherKek.VerifyAsync(token, Ct));
    }

    [Fact]
    public void The_grace_period_is_the_lifetime_plus_the_clock_skew()
    {
        var options = new AccessTokenSigningOptions();

        Assert.Equal(TimeSpan.FromMinutes(16), options.Grace);
    }

    /// <summary>The default grace period (15 minutes lifetime plus 60 seconds skew).</summary>
    private static TimeSpan AccessTokenSigningOptions_Grace() => new AccessTokenSigningOptions().Grace;

    [Fact]
    public async Task A_lost_rotation_race_signs_with_the_live_key_the_other_host_inserted()
    {
        var h = Create();
        var winnerId = Guid.NewGuid();
        var winnerKey = RandomNumberGenerator.GetBytes(32);
        var wrapped = await h.Wrapper.WrapAsync(winnerKey, winnerId.ToByteArray(), Ct);
        h.Store.ArrangeLostRace(new NewSigningKey(winnerId, _scope, wrapped, h.Wrapper.KekId, h.Time.GetUtcNow()));
        var deviceId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var token = await h.Signer.SignAsync(deviceId, humanId, h.Time.GetUtcNow().AddMinutes(15), Ct);

        Assert.Equal(winnerId.ToString("N"), token.Split('.')[1]);
        Assert.Equal(new AccessTokenClaims(deviceId, humanId), await h.Signer.VerifyAsync(token, Ct));
        Assert.Equal(winnerId, (await h.Store.GetLiveAsync(_scope, Ct))!.DekId);
    }

    [Fact]
    public async Task A_lost_rotation_race_leaves_no_row_of_the_losing_signer_live()
    {
        var h = Create();
        var winnerId = Guid.NewGuid();
        var wrapped = await h.Wrapper.WrapAsync(RandomNumberGenerator.GetBytes(32), winnerId.ToByteArray(), Ct);
        h.Store.ArrangeLostRace(new NewSigningKey(winnerId, _scope, wrapped, h.Wrapper.KekId, h.Time.GetUtcNow()));

        await h.Signer.SignAsync(Guid.NewGuid(), Guid.NewGuid(), h.Time.GetUtcNow().AddMinutes(15), Ct);

        var rows = await h.Store.ListAsync(_scope, Ct);
        Assert.Equal(winnerId, Assert.Single(rows, r => r.RetiredAt is null).DekId);
    }
}
