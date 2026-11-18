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
    private const string _scope = "access-token";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresTokenBudgetStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    private static NewSigningKey Key(DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), _scope, RandomNumberGenerator.GetBytes(60), "kek-1", createdAt);

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

        Assert.Equal(second.DekId, (await store.GetLiveAsync(_scope, Ct))!.DekId);
        var rows = await store.ListAsync(_scope, Ct);
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
        await store.ClearRetiredWrapsAsync(_scope, _t0.AddHours(1), Ct);

        var rows = await store.ListAsync(_scope, Ct);
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

        var live = await store.GetLiveAsync(_scope, Ct);
        Assert.NotNull(live);
        Assert.Equal(key.WrappedDek, live.WrappedDek);
        Assert.Equal("kek-1", live.KekId);
        Assert.Null(await store.GetLiveAsync("install", Ct));
    }

    [Fact]
    public async Task A_lost_rotation_race_returns_the_live_row_the_other_host_inserted()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var db = await PgTestDatabase.CreateAsync("signing", MigrateAsync, Ct);
        var store = new PostgresSigningKeyStore(db.DataSource);
        var first = Key(_t0);
        await store.RotateAsync(first, _t0, Ct);

        // Another host retires the live row and inserts its own, but has not committed yet.
        var other = Key(_t0.AddHours(1));
        await using var otherConnection = await db.DataSource.OpenConnectionAsync(Ct);
        await using var otherTx = await otherConnection.BeginTransactionAsync(Ct);
        await using (var retire = new NpgsqlCommand("UPDATE soul_dek SET retired_at = $2 WHERE scope = $1 AND retired_at IS NULL", otherConnection, otherTx))
        {
            retire.Parameters.Add(new NpgsqlParameter<string> { TypedValue = _scope });
            retire.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = _t0.AddHours(1) });
            await retire.ExecuteNonQueryAsync(Ct);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO soul_dek (dek_id, scope, wrapped_dek, kek_id, created_at) VALUES ($1, $2, $3, $4, $5)", otherConnection, otherTx))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = other.DekId });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = _scope });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = other.WrappedDek });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = other.KekId });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = other.CreatedAt });
            await insert.ExecuteNonQueryAsync(Ct);
        }

        // Our rotation blocks on the other host's row lock; once it commits, our insert hits the one-live index.
        var ours = Key(_t0.AddHours(1));
        var rotating = store.RotateAsync(ours, _t0.AddHours(1), Ct).AsTask();
        await otherTx.CommitAsync(Ct);
        var returned = await rotating;

        Assert.Equal(other.DekId, returned.DekId);
        var rows = await store.ListAsync(_scope, Ct);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.DekId == ours.DekId);
        Assert.Equal(other.DekId, (await store.GetLiveAsync(_scope, Ct))!.DekId);
    }
}
