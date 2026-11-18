using System.Security.Cryptography;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Identity.Signing;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Identity.Postgres.Tests;

/// <summary>The durable signing-key store over <c>soul_dek</c>. Needs the Docker or BAZAAR_TEST_PG PostgreSQL that the other store tests use.</summary>
public sealed class PostgresSigningKeyStoreTests
{
    private const string Scope = "access-token";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresTokenBudgetStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    private static NewSigningKey Key(DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), Scope, RandomNumberGenerator.GetBytes(60), "kek-1", createdAt);

    [Fact]
    public async Task Rotation_retires_the_live_row_and_leaves_exactly_one_live_key()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var db = await PgTestDatabase.CreateAsync("signing", MigrateAsync, Ct);
        var store = new PostgresSigningKeyStore(db.DataSource);
        var first = Key(_t0);
        var second = Key(_t0.AddDays(1));

        await store.RotateAsync(first, _t0, Ct);
        await store.RotateAsync(second, _t0.AddDays(1), Ct);

        Assert.Equal(second.DekId, (await store.GetLiveAsync(Scope, Ct))!.DekId);
        var rows = await store.ListAsync(Scope, Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(_t0.AddDays(1), rows.Single(r => r.DekId == first.DekId).RetiredAt);
        Assert.Null(rows.Single(r => r.DekId == second.DekId).RetiredAt);
    }

    [Fact]
    public async Task Clearing_drops_the_wrap_only_of_keys_retired_on_or_before_the_cutoff()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var db = await PgTestDatabase.CreateAsync("signing", MigrateAsync, Ct);
        var store = new PostgresSigningKeyStore(db.DataSource);
        var a = Key(_t0);
        var b = Key(_t0.AddDays(1));
        var c = Key(_t0.AddDays(2));
        await store.RotateAsync(a, _t0, Ct);
        await store.RotateAsync(b, _t0.AddHours(1), Ct);
        await store.RotateAsync(c, _t0.AddHours(2), Ct);

        // a retired at +1h, b at +2h; the cutoff +1h clears only a.
        await store.ClearRetiredWrapsAsync(Scope, _t0.AddHours(1), Ct);

        var rows = await store.ListAsync(Scope, Ct);
        Assert.Null(rows.Single(r => r.DekId == a.DekId).WrappedDek);
        Assert.NotNull(rows.Single(r => r.DekId == b.DekId).WrappedDek);
        Assert.NotNull(rows.Single(r => r.DekId == c.DekId).WrappedDek);
        Assert.Equal(_t0.AddHours(1), rows.Single(r => r.DekId == a.DekId).RetiredAt);
    }

    [Fact]
    public async Task The_wrapped_key_and_kek_id_survive_a_round_trip()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var db = await PgTestDatabase.CreateAsync("signing", MigrateAsync, Ct);
        var store = new PostgresSigningKeyStore(db.DataSource);
        var key = Key(_t0);

        await store.RotateAsync(key, _t0, Ct);

        var live = await store.GetLiveAsync(Scope, Ct);
        Assert.NotNull(live);
        Assert.Equal(key.WrappedDek, live.WrappedDek);
        Assert.Equal("kek-1", live.KekId);
        Assert.Null(await store.GetLiveAsync("install", Ct));
    }
}
