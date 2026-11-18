using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Identity.Tests.Signing;

public sealed class InMemorySigningKeyStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static NewSigningKey Key(DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), Scope: "access-token", WrappedDek: [1, 2, 3], KekId: "test-kek", CreatedAt: createdAt);

    [Fact]
    public async Task Rotating_retires_the_previous_live_row_and_leaves_one_live()
    {
        var store = new InMemorySigningKeyStore();
        var first = Key(_t0);
        var second = Key(_t0.AddDays(1));

        await store.RotateAsync(first, _t0, Ct);
        await store.RotateAsync(second, _t0.AddDays(1), Ct);

        Assert.Equal(second.DekId, (await store.GetLiveAsync("access-token", Ct))!.DekId);
        var rows = await store.ListAsync("access-token", Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(_t0.AddDays(1), rows.Single(r => r.DekId == first.DekId).RetiredAt);
    }

    [Fact]
    public async Task Clearing_drops_only_the_wraps_of_rows_retired_on_or_before_the_cutoff()
    {
        var store = new InMemorySigningKeyStore();
        var a = Key(_t0);
        var b = Key(_t0.AddDays(1));
        var c = Key(_t0.AddDays(2));
        await store.RotateAsync(a, _t0, Ct);
        await store.RotateAsync(b, _t0.AddHours(1), Ct);
        await store.RotateAsync(c, _t0.AddHours(2), Ct);

        await store.ClearRetiredWrapsAsync("access-token", _t0.AddHours(1), Ct); // a retired at +1h, b at +2h

        var rows = await store.ListAsync("access-token", Ct);
        Assert.Null(rows.Single(r => r.DekId == a.DekId).WrappedDek);
        Assert.NotNull(rows.Single(r => r.DekId == b.DekId).WrappedDek);
        Assert.NotNull(rows.Single(r => r.DekId == c.DekId).WrappedDek);
    }

    [Fact]
    public async Task Scopes_are_independent()
    {
        var store = new InMemorySigningKeyStore();
        await store.RotateAsync(Key(_t0), _t0, Ct);

        Assert.Null(await store.GetLiveAsync("install", Ct));
    }

    [Fact]
    public async Task A_lost_rotation_race_returns_the_winning_live_row_and_inserts_nothing()
    {
        var store = new InMemorySigningKeyStore();
        var first = Key(_t0);
        await store.RotateAsync(first, _t0, Ct);
        var winner = Key(_t0.AddHours(1));
        var loser = Key(_t0.AddHours(1));
        store.ArrangeLostRace(winner);

        var returned = await store.RotateAsync(loser, _t0.AddHours(1), Ct);

        Assert.Equal(winner.DekId, returned.DekId);
        Assert.Equal(winner.DekId, (await store.GetLiveAsync("access-token", Ct))!.DekId);
        var rows = await store.ListAsync("access-token", Ct);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.DekId == loser.DekId);
        Assert.Equal(_t0.AddHours(1), rows.Single(r => r.DekId == first.DekId).RetiredAt);
    }

    [Fact]
    public async Task An_uncontended_rotation_returns_the_new_live_row()
    {
        var store = new InMemorySigningKeyStore();
        var key = Key(_t0);

        var returned = await store.RotateAsync(key, _t0, Ct);

        Assert.Equal(key.DekId, returned.DekId);
        Assert.Null(returned.RetiredAt);
    }
}
