using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.WorkLife;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Presence.Postgres.Tests;

/// <summary>
/// The store against PostgreSQL. The server comes from <c>BAZAAR_TEST_PG</c>, or a Testcontainers container when it is unset
/// (<see cref="PgSource"/>). Each test gets its own database migrated with the presence namespace only.
/// </summary>
public class PgPresenceStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");

    private static PresenceRow Row(Guid agent, long revision, string presence = "Working", string? label = null) => new(
        agent, revision, PresenceAvailability.OnShift, PresenceOverlay.Throttled | PresenceOverlay.Covered, presence,
        PresenceActivityKind.Tool, Guid.Parse("00000000-0000-7000-8000-0000000000c1"), Guid.Parse("00000000-0000-7000-8000-0000000000d1"),
        label, _t0, _t0.AddSeconds(revision));

    private static async Task<PgTestDatabase> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("presence", MigrateAsync, ct);
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        await new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PgPresenceStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    [Fact]
    public async Task A_stored_row_reads_back_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PgPresenceStore(db.DataSource);
        var row = Row(_ada, 1, label: "fixing the build");

        Assert.True(await store.UpsertAsync(row, ct));

        Assert.Equal(row, await store.GetAsync(_ada, ct));
    }

    [Fact]
    public async Task A_lower_or_equal_revision_is_refused_and_a_higher_one_replaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PgPresenceStore(db.DataSource);
        await store.UpsertAsync(Row(_ada, 5, "Five"), ct);

        Assert.False(await store.UpsertAsync(Row(_ada, 5, "Same"), ct));
        Assert.False(await store.UpsertAsync(Row(_ada, 4, "Older"), ct));
        Assert.Equal("Five", (await store.GetAsync(_ada, ct))!.Presence);

        Assert.True(await store.UpsertAsync(Row(_ada, 6, "Six"), ct));
        Assert.Equal("Six", (await store.GetAsync(_ada, ct))!.Presence);
    }

    [Fact]
    public async Task Get_all_returns_one_row_per_agent_and_a_missing_agent_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PgPresenceStore(db.DataSource);
        await store.UpsertAsync(Row(_ada, 1), ct);
        await store.UpsertAsync(Row(_bob, 3), ct);
        await store.UpsertAsync(Row(_ada, 2), ct);

        var all = await store.GetAllAsync(ct);

        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Single(r => r.AgentId == _ada).Revision);
        Assert.Null(await store.GetAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task The_table_refuses_an_unknown_availability()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO agent_activity (agent_id, revision, availability, overlay, presence, activity_kind, since, updated_at) " +
            "VALUES (gen_random_uuid(), 1, 'napping', 'none', 'x', 'idle', now(), now())");

        await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync(ct));
    }
}
