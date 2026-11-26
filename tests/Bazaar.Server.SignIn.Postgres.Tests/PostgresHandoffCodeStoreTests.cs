using System.Security.Cryptography;
using System.Text;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.WebAdmin;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.SignIn.Postgres.Tests;

/// <summary>
/// The migration as embedded in the assembly (no database needed).
/// </summary>
public class HandoffMigrationTests
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(PostgresHandoffCodeStore).Assembly);

    [Fact]
    public void Signin_namespace_holds_migration_0001_handoff_code()
    {
        Assert.Contains("signin", _source.Namespaces());
        var file = Assert.Single(_source.Load("signin"), f => f.Version == 1);
        Assert.Equal("handoff_code", file.Name);
        Assert.False(file.NoTransaction);
        Assert.Contains("CREATE TABLE handoff_code (", file.Sql);
    }

    [Fact]
    public void The_migration_stays_out_of_the_frozen_common_namespace()
    {
        Assert.Empty(_source.Load(MigrationRunner.Common));
    }
}

/// <summary>
/// The store against PostgreSQL. The server comes from <c>BAZAAR_TEST_PG</c>, or a Testcontainers container when it is unset
/// (<see cref="PgSource"/>); on Windows without <c>BAZAAR_TEST_PG</c> the tests skip. Each test gets its own database.
/// </summary>
public class PostgresHandoffCodeStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly SignInIdentity _who = new(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());

    private static async Task<PgTestDatabase> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("signin", MigrateAsync, ct);
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        await new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresHandoffCodeStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    private static async Task<long> CountAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM handoff_code");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task Migration_reruns_as_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);

        await MigrateAsync(db.DataSource, ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM schema_migrations WHERE namespace = 'signin' AND version = 1 AND name = 'handoff_code'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Code_redeems_once_and_returns_the_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        var code = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);

        Assert.Equal(_who, await store.RedeemAsync(code, SignInAudiences.Admin, _t0.AddSeconds(1), ct));
        Assert.Null(await store.RedeemAsync(code, SignInAudiences.Admin, _t0.AddSeconds(2), ct));
    }

    [Fact]
    public async Task Code_is_valid_until_60_seconds_and_not_at_60()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        var ok = await store.IssueAsync(_who, SignInAudiences.Native, _t0, ct);
        var late = await store.IssueAsync(_who, SignInAudiences.Native, _t0, ct);

        Assert.NotNull(await store.RedeemAsync(ok, SignInAudiences.Native, _t0.AddSeconds(59.9), ct));
        Assert.Null(await store.RedeemAsync(late, SignInAudiences.Native, _t0.AddSeconds(60), ct));
    }

    [Fact]
    public async Task Wrong_audience_burns_the_code()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        var admin = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);

        Assert.Null(await store.RedeemAsync(admin, SignInAudiences.Native, _t0, ct));
        Assert.Null(await store.RedeemAsync(admin, SignInAudiences.Admin, _t0, ct));
    }

    [Fact]
    public async Task Only_the_hash_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        var code = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);

        await using var cmd = db.DataSource.CreateCommand("SELECT code_hash FROM handoff_code");
        var stored = (string)(await cmd.ExecuteScalarAsync(ct))!;

        Assert.NotEqual(code, stored);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))), stored);
    }

    [Fact]
    public async Task A_second_store_on_the_same_database_redeems_what_the_first_issued()
    {
        // The two-process case: the server issues, bazaar-tui redeems.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var server = new PostgresHandoffCodeStore(db.DataSource);
        await using var other = NpgsqlDataSource.Create(db.DataSource.ConnectionString);
        var tui = new PostgresHandoffCodeStore(other);
        var code = await server.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);

        var redeemer = new PostgresAdminHandoffRedeemer(tui, new FixedTime(_t0.AddSeconds(5)));

        Assert.Equal(new AdminIdentity(_who.PrincipalId, _who.Kind, _who.DeviceId), await redeemer.RedeemAsync(code, ct));
        Assert.Null(await server.RedeemAsync(code, SignInAudiences.Admin, _t0.AddSeconds(6), ct));
    }

    [Fact]
    public async Task Concurrent_redeems_of_one_code_have_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        var code = await store.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.RedeemAsync(code, SignInAudiences.Admin, _t0.AddSeconds(1), ct), ct)));

        Assert.Single(results, r => r is not null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    public async Task Unknown_codes_redeem_to_null(string code)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);

        Assert.Null(await store.RedeemAsync(code, SignInAudiences.Admin, _t0, ct));
        Assert.Null(await store.RedeemAsync(new string('a', 5000), SignInAudiences.Admin, _t0, ct));
    }

    [Fact]
    public async Task Every_principal_kind_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        foreach (var kind in Enum.GetValues<PrincipalKind>())
        {
            var who = new SignInIdentity(Guid.NewGuid(), kind, Guid.NewGuid());
            var code = await store.IssueAsync(who, SignInAudiences.Native, _t0, ct);
            Assert.Equal(who, await store.RedeemAsync(code, SignInAudiences.Native, _t0, ct));
        }
    }

    [Fact]
    public async Task Issue_sweeps_expired_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);
        await store.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);
        await store.IssueAsync(_who, SignInAudiences.Admin, _t0, ct);

        await store.IssueAsync(_who, SignInAudiences.Admin, _t0.AddMinutes(5), ct);

        Assert.Equal(1L, await CountAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Unknown_audience_is_refused_at_issue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresHandoffCodeStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => store.IssueAsync(_who, "other", _t0, ct));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
