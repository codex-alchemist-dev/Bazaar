using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Company.Tests;

/// <summary>The Postgres stores over a real database: what they write, idempotency, refusals and rollback.</summary>
public sealed class PostgresCompanyStoresTests
{
    private const string _skip = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";
    private const string _bundle = "secret://bazaar/owner/recovery-bundle";

    private static readonly CompanyProfileRequest _profile = new("Example Studio", "example-studio", "en", "UTC");
    private static readonly OwnerRequest _owner = new("Owner", true, _bundle, false);

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string from, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"SELECT count(*) FROM {from}");
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task The_profile_is_created_read_back_and_a_second_ensure_changes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresCompanyProfileStore(db.DataSource);

        var first = await store.EnsureAsync(_profile, ct);
        var second = await store.EnsureAsync(_profile, ct);

        Assert.True(first.Succeeded);
        Assert.True(first.Created);
        Assert.True(second.Succeeded);
        Assert.False(second.Created);
        var read = await new PostgresCompanyProfileReader(db.DataSource).GetAsync(ct);
        Assert.Equal("example-studio", read!.Slug);
        Assert.Equal("Example Studio", read.Name);
        Assert.Equal(1, await CountAsync(db.DataSource, "company_profile", ct));
    }

    [Fact]
    public async Task A_profile_with_other_values_is_refused_and_left_alone()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresCompanyProfileStore(db.DataSource);
        await store.EnsureAsync(_profile, ct);

        var result = await store.EnsureAsync(_profile with { Slug = "other" }, ct);

        Assert.False(result.Succeeded);
        Assert.Equal("example-studio", (await new PostgresCompanyProfileReader(db.DataSource).GetAsync(ct))!.Slug);
    }

    [Fact]
    public async Task The_reader_returns_null_before_setup_created_the_company()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);

        Assert.Null(await new PostgresCompanyProfileReader(db.DataSource).GetAsync(ct));
    }

    [Fact]
    public async Task Profile_rollback_removes_the_row_only_for_the_same_slug()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresCompanyProfileStore(db.DataSource);
        await store.EnsureAsync(_profile, ct);

        await store.RemoveAsync("another-slug", ct);
        Assert.Equal(1, await CountAsync(db.DataSource, "company_profile", ct));
        await store.RemoveAsync("example-studio", ct);
        Assert.Equal(0, await CountAsync(db.DataSource, "company_profile", ct));
    }

    [Fact]
    public async Task The_owner_is_created_active_with_the_recorded_choices_and_a_second_ensure_changes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresOwnerStore(db.DataSource);

        var first = await store.EnsureAsync(_owner, ct);
        var second = await store.EnsureAsync(_owner, ct);

        Assert.True(first.Succeeded);
        Assert.NotNull(first.Outcome!.OwnerCreatedId);
        Assert.True(first.Outcome.ChoicesCreated);
        Assert.True(second.Succeeded);
        Assert.True(second.Outcome!.IsEmpty);
        Assert.Equal(1, await CountAsync(db.DataSource, "human WHERE role = 'owner' AND status = 'active'", ct));
        await using var command = db.DataSource.CreateCommand("SELECT recovery_bundle_ref FROM owner_profile");
        Assert.Equal(_bundle, (string?)await command.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task An_owner_with_another_name_and_different_choices_are_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresOwnerStore(db.DataSource);
        await store.EnsureAsync(_owner, ct);

        Assert.False((await store.EnsureAsync(_owner with { DisplayName = "Someone Else" }, ct)).Succeeded);
        Assert.False((await store.EnsureAsync(_owner with { RequireSecondFactor = false }, ct)).Succeeded);
        Assert.Equal(1, await CountAsync(db.DataSource, "human WHERE role = 'owner'", ct));
    }

    [Fact]
    public async Task A_bundle_reference_that_is_not_a_secret_reference_is_refused_by_the_table()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);

        await Assert.ThrowsAsync<PostgresException>(() => new PostgresOwnerStore(db.DataSource).EnsureAsync(_owner with { RecoveryBundleRef = "not-a-reference" }, ct));
    }

    [Fact]
    public async Task Owner_rollback_removes_what_the_outcome_names()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresOwnerStore(db.DataSource);
        var result = await store.EnsureAsync(_owner, ct);

        await store.RollbackAsync(result.Outcome!, ct);

        Assert.Equal(0, await CountAsync(db.DataSource, "human", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, "owner_profile", ct));
    }

    [Fact]
    public async Task Owner_rollback_of_an_empty_outcome_removes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var store = new PostgresOwnerStore(db.DataSource);
        await store.EnsureAsync(_owner, ct);

        await store.RollbackAsync(new OwnerOutcome(null, false), ct);

        Assert.Equal(1, await CountAsync(db.DataSource, "human", ct));
    }
}
