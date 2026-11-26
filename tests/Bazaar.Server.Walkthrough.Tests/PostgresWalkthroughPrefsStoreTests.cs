using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Walkthrough.Tests;

public sealed class PostgresWalkthroughPrefsStoreTests
{
    private static readonly WalkthroughPrefs _sample = new(true, false, "MeetAgent", ["CreateCompany"]);

    [Fact]
    public async Task Prefs_round_trip_per_person_and_a_save_replaces_the_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await WalkthroughDatabase.CreateAsync(ct);
        var store = new PostgresWalkthroughPrefsStore(db.DataSource);

        Assert.Null(await store.GetAsync("alice", ct));

        await store.SetAsync("alice", _sample, ct);
        var read = await store.GetAsync("alice", ct);
        Assert.Equal(_sample.Dismissed, read!.Dismissed);
        Assert.Equal("MeetAgent", read.Current);
        Assert.Equal(["CreateCompany"], read.Completed);
        Assert.Null(await store.GetAsync("bob", ct));

        await store.SetAsync("alice", new WalkthroughPrefs(false, true, "Done", ["CreateCompany", "MeetAgent", "FirstTask"]), ct);
        var again = await new PostgresWalkthroughPrefsStore(db.DataSource).GetAsync("alice", ct);
        Assert.True(again!.Finished);
        Assert.Equal("Done", again.Current);
        Assert.Equal(3, again.Completed.Count);
    }

    [Fact]
    public async Task The_table_refuses_a_step_name_the_route_would_never_send()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await WalkthroughDatabase.CreateAsync(ct);
        var store = new PostgresWalkthroughPrefsStore(db.DataSource);

        await Assert.ThrowsAsync<PostgresException>(() => store.SetAsync("alice", new WalkthroughPrefs(false, false, "Bogus", []), ct));
        await Assert.ThrowsAsync<PostgresException>(() => store.SetAsync("alice", new WalkthroughPrefs(false, false, "Done", ["Nope"]), ct));
    }
}
