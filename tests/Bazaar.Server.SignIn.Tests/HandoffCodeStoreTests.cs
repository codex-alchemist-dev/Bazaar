using Bazaar.Roles.Abstractions;
using Bazaar.Server.SignIn.Handoff;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.SignIn.Tests;

public sealed class HandoffCodeStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly SignInIdentity _who = new(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());

    [Fact]
    public async Task Code_redeems_once()
    {
        var store = new InMemoryHandoffCodeStore();
        var code = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken);

        Assert.Equal(_who, await store.RedeemAsync(code, SignInAudiences.Admin, _t0.AddSeconds(1), TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(code, SignInAudiences.Admin, _t0.AddSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Code_is_valid_until_60_seconds_and_not_at_60()
    {
        var store = new InMemoryHandoffCodeStore();
        var ok = await store.IssueAsync(_who, SignInAudiences.Native, _t0, TestContext.Current.CancellationToken);
        var late = await store.IssueAsync(_who, SignInAudiences.Native, _t0, TestContext.Current.CancellationToken);

        Assert.NotNull(await store.RedeemAsync(ok, SignInAudiences.Native, _t0.AddSeconds(59.9), TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(late, SignInAudiences.Native, _t0.AddSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Code_is_bound_to_its_audience_and_a_wrong_audience_burns_it()
    {
        var store = new InMemoryHandoffCodeStore();
        var native = await store.IssueAsync(_who, SignInAudiences.Native, _t0, TestContext.Current.CancellationToken);
        var admin = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken);

        Assert.Null(await store.RedeemAsync(native, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(native, SignInAudiences.Native, _t0, TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(admin, SignInAudiences.Native, _t0, TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(admin, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    public async Task Unknown_codes_redeem_to_null(string code)
    {
        var store = new InMemoryHandoffCodeStore();
        Assert.Null(await store.RedeemAsync(code, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(new string('a', 5000), SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_audience_is_refused_at_issue()
    {
        var store = new InMemoryHandoffCodeStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.IssueAsync(_who, "other", _t0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Store_is_bounded_and_drops_expired_entries_first()
    {
        var store = new InMemoryHandoffCodeStore(capacity: 3);
        var old = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken);
        await store.IssueAsync(_who, SignInAudiences.Admin, _t0.AddSeconds(50), TestContext.Current.CancellationToken);
        await store.IssueAsync(_who, SignInAudiences.Admin, _t0.AddSeconds(51), TestContext.Current.CancellationToken);
        var fresh = await store.IssueAsync(_who, SignInAudiences.Admin, _t0.AddSeconds(61), TestContext.Current.CancellationToken);

        Assert.Equal(3, store.Count);
        Assert.Null(await store.RedeemAsync(old, SignInAudiences.Admin, _t0.AddSeconds(61), TestContext.Current.CancellationToken));
        Assert.NotNull(await store.RedeemAsync(fresh, SignInAudiences.Admin, _t0.AddSeconds(61), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Store_evicts_the_oldest_when_full_of_live_codes()
    {
        var store = new InMemoryHandoffCodeStore(capacity: 2);
        var first = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, TestContext.Current.CancellationToken);
        await store.IssueAsync(_who, SignInAudiences.Admin, _t0.AddSeconds(1), TestContext.Current.CancellationToken);
        await store.IssueAsync(_who, SignInAudiences.Admin, _t0.AddSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Count);
        Assert.Null(await store.RedeemAsync(first, SignInAudiences.Admin, _t0.AddSeconds(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admin_redeemer_adapts_only_admin_codes()
    {
        var store = new InMemoryHandoffCodeStore();
        var redeemer = new HandoffRedeemer(store, TimeProvider.System);
        var native = await store.IssueAsync(_who, SignInAudiences.Native, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var admin = await store.IssueAsync(_who, SignInAudiences.Admin, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Null(await redeemer.RedeemAsync(native, TestContext.Current.CancellationToken));
        Assert.Equal(new AdminIdentity(_who.PrincipalId, PrincipalKind.Human, _who.DeviceId), await redeemer.RedeemAsync(admin, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Challenge_is_single_use_and_expires()
    {
        var store = new InMemoryPasskeyChallengeStore();
        var c = await store.IssueAsync(_t0, TestContext.Current.CancellationToken);

        Assert.Equal(c.Challenge, await store.ConsumeAsync(c.Id, _t0.AddSeconds(5), TestContext.Current.CancellationToken));
        Assert.Null(await store.ConsumeAsync(c.Id, _t0.AddSeconds(6), TestContext.Current.CancellationToken));

        var late = await store.IssueAsync(_t0, TestContext.Current.CancellationToken);
        Assert.Null(await store.ConsumeAsync(late.Id, _t0.AddSeconds(60), TestContext.Current.CancellationToken));
    }
}
