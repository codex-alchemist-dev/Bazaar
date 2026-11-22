using Bazaar.Server.Agents.Data;
using Bazaar.Server.Setup;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Company.Tests;

/// <summary>The Postgres seeder over a real database: what it writes, that a second seed changes nothing, refusals, and rollback.</summary>
public sealed class PostgresCompanySeederTests
{
    private const string _skip = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static CompanySeedRequest Request(string slug = "example-studio", string owner = "Owner", params string[] agents) =>
        new("Example Studio", slug, "en", "UTC", owner, true, agents);

    private static PostgresCompanySeeder Seeder(NpgsqlDataSource dataSource) => new(dataSource, new AgentRepository(dataSource));

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string table, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task Seed_creates_the_profile_the_owner_and_agents_in_the_created_state()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);

        var result = await Seeder(db.DataSource).SeedAsync(Request(agents: ["Charles", "Ada"]), ct);

        Assert.True(result.Succeeded, result.Refusal);
        Assert.True(result.Outcome!.CompanyCreated);
        Assert.NotNull(result.Outcome.OwnerCreatedId);
        Assert.Equal(["Charles", "Ada"], result.Outcome.AgentsCreated.Select(agent => agent.Name));
        Assert.Equal(1, await CountAsync(db.DataSource, "company_profile", ct));
        Assert.Equal(1, await CountAsync(db.DataSource, "human WHERE role = 'owner'", ct));
        Assert.Equal(2, await CountAsync(db.DataSource, "agent WHERE state = 'created'", ct));
        var profile = await new PostgresCompanyProfileReader(db.DataSource).GetAsync(ct);
        Assert.Equal("example-studio", profile!.Slug);
        Assert.True(profile.OwnerSecondFactorRequired);
    }

    [Fact]
    public async Task A_second_seed_creates_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var seeder = Seeder(db.DataSource);
        await seeder.SeedAsync(Request(agents: ["Charles"]), ct);

        var second = await seeder.SeedAsync(Request(agents: ["charles"]), ct);

        Assert.True(second.Succeeded, second.Refusal);
        Assert.True(second.Outcome!.IsEmpty);
        Assert.Single(second.AgentsFound);
        Assert.Equal(1, await CountAsync(db.DataSource, "agent", ct));
    }

    [Fact]
    public async Task A_company_with_another_slug_is_refused_and_nothing_is_added()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var seeder = Seeder(db.DataSource);
        await seeder.SeedAsync(Request(), ct);

        var result = await seeder.SeedAsync(Request(slug: "other", agents: ["Charles"]), ct);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await CountAsync(db.DataSource, "agent", ct));
    }

    [Fact]
    public async Task An_owner_with_another_name_is_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var seeder = Seeder(db.DataSource);
        await seeder.SeedAsync(Request(), ct);

        var result = await seeder.SeedAsync(Request(owner: "Someone else"), ct);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await CountAsync(db.DataSource, "human", ct));
    }

    [Fact]
    public async Task Rollback_removes_what_the_outcome_names_and_only_that()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var seeder = Seeder(db.DataSource);
        var result = await seeder.SeedAsync(Request(agents: ["Charles"]), ct);

        await seeder.RollbackAsync(result.Outcome!, ct);

        Assert.Equal(0, await CountAsync(db.DataSource, "company_profile", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, "human", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, "agent", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, "principal_handle", ct));
    }

    [Fact]
    public async Task Rollback_leaves_an_agent_that_has_left_the_created_state()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);
        var seeder = Seeder(db.DataSource);
        var result = await seeder.SeedAsync(Request(agents: ["Charles"]), ct);
        await using (var update = db.DataSource.CreateCommand("UPDATE agent SET state = 'onboarding'"))
        {
            await update.ExecuteNonQueryAsync(ct);
        }

        await seeder.RollbackAsync(result.Outcome!, ct);

        Assert.Equal(1, await CountAsync(db.DataSource, "agent", ct));
        Assert.Equal(0, await CountAsync(db.DataSource, "company_profile", ct));
    }

    [Fact]
    public async Task Preview_reports_without_writing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CompanyDatabase.CreateAsync(ct);

        var preview = await Seeder(db.DataSource).PreviewAsync(Request(agents: ["Charles"]), ct);

        Assert.False(preview.CompanyExists);
        Assert.Equal(["Charles"], preview.AgentsToCreate);
        Assert.Equal(0, await CountAsync(db.DataSource, "company_profile", ct));
    }
}
