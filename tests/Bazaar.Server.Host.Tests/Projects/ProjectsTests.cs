using Bazaar.Core;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Projects;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Host.Tests.Projects;

/// <summary>Projects and membership (doc 93) against a real PostgreSQL: the membership source, the audited writes and the host wiring.</summary>
public sealed class ProjectsTests(ProjectsDb db) : IClassFixture<ProjectsDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _lender = Guid.NewGuid();
    private readonly Guid _human = Guid.NewGuid();

    private async Task<(PgProjectStore Store, PgProjectMembership Membership, FakeTimeProvider Clock)> FreshAsync(ProjectsOptions? options = null)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await db.ClearAsync();
        var clock = new FakeTimeProvider(_t0);
        options ??= new ProjectsOptions();
        var store = new PgProjectStore(db.DataSource, new PostgresAuditLog(db.DataSource), options, clock);
        return (store, new PgProjectMembership(db.DataSource, options, clock), clock);
    }

    private VaultPrincipal AgentPrincipal => VaultPrincipal.Agent(_agent.ToString("D"));

    private async Task LendAsync(Guid projectId, Guid toAgent, DateTimeOffset start, DateTimeOffset? end, string kind = "lent_to")
    {
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO org_relation (id, kind, from_agent_id, to_agent_id, project_id, starts_at, ends_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _lender });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = toAgent });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = projectId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = start.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime?> { TypedValue = end?.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Guid> CreateAsync(PgProjectStore store, string slug = "alpha")
    {
        Assert.True((await store.CreateAsync(ProjectActor.Owner(), slug, "Alpha", Ct)).Succeeded);
        return (await store.GetAsync(slug, Ct))!.Id;
    }

    // The audit log is append-only and shared by the class, so a test reads only the entries after its own baseline.
    private async Task<long> AuditBaselineAsync()
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT COALESCE(MAX(seq), 0) FROM audit_log");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<string>> AuditKindsAsync(long after)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT kind FROM audit_log WHERE seq > {after} AND kind LIKE 'project.%' ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var kinds = new List<string>();
        while (await reader.ReadAsync(Ct))
            kinds.Add(reader.GetString(0));
        return kinds;
    }

    [Fact]
    public async Task Member_row_makes_an_agent_a_member()
    {
        var (store, membership, _) = await FreshAsync();
        await CreateAsync(store);

        Assert.Empty(membership.ProjectsOf(AgentPrincipal));
        Assert.True((await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct)).Succeeded);

        Assert.Equal(["alpha"], membership.ProjectsOf(AgentPrincipal));
        Assert.True(membership.IsMember(AgentPrincipal, "alpha"));
        Assert.False(membership.IsMember(AgentPrincipal, "beta"));
        // A cover answers its primary's projects.
        Assert.Equal(["alpha"], membership.ProjectsOf(VaultPrincipal.Cover(Guid.NewGuid().ToString("D"), _agent.ToString("D"))));
    }

    [Fact]
    public async Task Human_counts_only_through_a_member_row()
    {
        var (store, membership, _) = await FreshAsync();
        await CreateAsync(store);
        var human = VaultPrincipal.MemberHuman(_human.ToString("D"));

        Assert.Empty(membership.ProjectsOf(human));
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.User, _human, ProjectRole.Member, Ct);

        Assert.Equal(["alpha"], membership.ProjectsOf(human));
        // The owner and maintenance read through VaultPathAccess, not here.
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Owner()));
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Maintenance()));
    }

    [Fact]
    public async Task Current_loan_grants_membership_and_an_expired_loan_stops_without_cleanup()
    {
        var (store, membership, clock) = await FreshAsync();
        var projectId = await CreateAsync(store);
        await LendAsync(projectId, _agent, _t0.AddHours(-1), _t0.AddHours(2));

        Assert.Equal(["alpha"], membership.ProjectsOf(AgentPrincipal));

        clock.Advance(TimeSpan.FromHours(3));

        Assert.Empty(membership.ProjectsOf(AgentPrincipal));
    }

    [Fact]
    public async Task Future_loan_and_reports_to_and_the_lender_grant_nothing()
    {
        var (store, membership, _) = await FreshAsync();
        var projectId = await CreateAsync(store);
        await LendAsync(projectId, _agent, _t0.AddHours(1), null);

        Assert.Empty(membership.ProjectsOf(AgentPrincipal));

        // A reports_to edge grants nothing, even scoped to the project, and the lent agent (from side) is not the borrower.
        var other = Guid.NewGuid();
        await LendAsync(projectId, other, _t0.AddHours(-1), null, kind: "reports_to");
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Agent(other.ToString("D"))));
        await LendAsync(projectId, Guid.NewGuid(), _t0.AddHours(-1), null);
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Agent(_lender.ToString("D"))));
    }

    [Fact]
    public async Task Loans_do_not_count_when_the_setting_is_off()
    {
        var (store, membership, _) = await FreshAsync(new ProjectsOptions(LoansGrantRead: false));
        var projectId = await CreateAsync(store);
        await LendAsync(projectId, _agent, _t0.AddHours(-1), null);

        Assert.Empty(membership.ProjectsOf(AgentPrincipal));

        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct);
        Assert.Equal(["alpha"], membership.ProjectsOf(AgentPrincipal));
    }

    [Fact]
    public async Task Ended_membership_stops_granting_and_can_be_re_added()
    {
        var (store, membership, clock) = await FreshAsync();
        await CreateAsync(store);
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True((await store.EndMembershipAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, Ct)).Succeeded);
        Assert.Empty(membership.ProjectsOf(AgentPrincipal));
        Assert.Equal(ProjectError.NotMember, (await store.EndMembershipAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, Ct)).Error);
        Assert.Single(await store.ListMembersAsync("alpha", Ct));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Lead, Ct)).Succeeded);
        Assert.Equal(["alpha"], membership.ProjectsOf(AgentPrincipal));
        Assert.Equal(ProjectError.AlreadyMember, (await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct)).Error);
    }

    [Fact]
    public async Task Unknown_principal_and_malformed_ids_and_slugs_answer_no_membership()
    {
        var (store, membership, _) = await FreshAsync();
        await CreateAsync(store);

        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Agent(Guid.NewGuid().ToString("D"))));
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Agent("not-a-guid")));
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Agent(Guid.Empty.ToString("D"))));
        Assert.False(membership.IsMember(AgentPrincipal, "Alpha"));
        Assert.False(membership.IsMember(AgentPrincipal, "../alpha"));
        Assert.False(membership.IsMember(AgentPrincipal, ""));
        Assert.False(membership.IsMember(AgentPrincipal, new string('a', 64)));
        Assert.Equal(ProjectError.InvalidSlug, (await store.CreateAsync(ProjectActor.Owner(), "Bad Slug", "x", Ct)).Error);
        Assert.Equal(ProjectError.InvalidSlug, (await store.CreateAsync(ProjectActor.Owner(), "-lead", "x", Ct)).Error);
    }

    [Fact]
    public async Task Database_error_fails_closed()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var broken = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1;Command Timeout=1");
        var membership = new PgProjectMembership(broken, new ProjectsOptions());

        Assert.Empty(membership.ProjectsOf(AgentPrincipal));
        Assert.False(membership.IsMember(AgentPrincipal, "alpha"));
    }

    [Fact]
    public async Task Archived_project_keeps_read_for_members_and_refuses_new_members()
    {
        var (store, membership, _) = await FreshAsync();
        await CreateAsync(store);
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct);

        Assert.True((await store.ArchiveAsync(ProjectActor.Owner(), "alpha", Ct)).Succeeded);

        Assert.Equal(["alpha"], membership.ProjectsOf(AgentPrincipal));
        Assert.Equal(ProjectState.Archived, (await store.GetAsync("alpha", Ct))!.State);
        Assert.Equal(ProjectError.Archived, (await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.User, _human, ProjectRole.Member, Ct)).Error);
        Assert.Equal(ProjectError.Archived, (await store.ArchiveAsync(ProjectActor.Owner(), "alpha", Ct)).Error);
        // Ending removes access, so it is still allowed; archiving deleted nothing.
        Assert.True((await store.EndMembershipAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, Ct)).Succeeded);
    }

    [Fact]
    public async Task Writes_follow_the_actor_rules_and_slugs_are_never_reused()
    {
        var (store, _, _) = await FreshAsync();
        var lead = Guid.NewGuid();

        Assert.Equal(ProjectError.Forbidden, (await store.CreateAsync(ProjectActor.Agent(lead), "alpha", "Alpha", Ct)).Error);
        Assert.Equal(ProjectError.InvalidTitle, (await store.CreateAsync(ProjectActor.Owner(), "alpha", "  ", Ct)).Error);
        await CreateAsync(store);
        Assert.Equal(ProjectError.SlugTaken, (await store.CreateAsync(ProjectActor.Owner(), "alpha", "Again", Ct)).Error);
        Assert.Equal(ProjectError.NotFound, (await store.AddMemberAsync(ProjectActor.Owner(), "beta", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct)).Error);
        Assert.Equal(ProjectError.InvalidPrincipal, (await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, Guid.Empty, ProjectRole.Member, Ct)).Error);

        // A plain member may not add; a lead may.
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct);
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, lead, ProjectRole.Lead, Ct);
        Assert.Equal(ProjectError.Forbidden, (await store.AddMemberAsync(ProjectActor.Agent(_agent), "alpha", ProjectPrincipalKind.User, _human, ProjectRole.Member, Ct)).Error);
        Assert.True((await store.AddMemberAsync(ProjectActor.Agent(lead), "alpha", ProjectPrincipalKind.User, _human, ProjectRole.Member, Ct)).Succeeded);
        Assert.Equal(ProjectError.Forbidden, (await store.AddMemberAsync(ProjectActor.Agent(Guid.NewGuid()), "alpha", ProjectPrincipalKind.User, Guid.NewGuid(), ProjectRole.Member, Ct)).Error);
    }

    [Fact]
    public async Task Owner_only_adders_setting_tightens_the_lead_rule()
    {
        var (store, _, _) = await FreshAsync(new ProjectsOptions(MemberAdders: MemberAdders.OwnerOnly));
        var lead = Guid.NewGuid();
        await CreateAsync(store);
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, lead, ProjectRole.Lead, Ct);

        Assert.Equal(ProjectError.Forbidden, (await store.AddMemberAsync(ProjectActor.Agent(lead), "alpha", ProjectPrincipalKind.User, _human, ProjectRole.Member, Ct)).Error);
    }

    [Fact]
    public async Task Every_applied_write_is_audited_and_refusals_are_not()
    {
        var (store, _, clock) = await FreshAsync();
        var baseline = await AuditBaselineAsync();
        await CreateAsync(store);
        await store.AddMemberAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, ProjectRole.Member, Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.EndMembershipAsync(ProjectActor.Owner(), "alpha", ProjectPrincipalKind.Agent, _agent, Ct);
        await store.ArchiveAsync(ProjectActor.Owner(), "alpha", Ct);
        await store.CreateAsync(ProjectActor.Owner(), "alpha", "dup", Ct);
        await store.AddMemberAsync(ProjectActor.Agent(_agent), "alpha", ProjectPrincipalKind.User, _human, ProjectRole.Member, Ct);

        Assert.Equal(["project.created", "project.member_added", "project.member_ended", "project.archived"], await AuditKindsAsync(baseline));
    }

    [Fact]
    public async Task Slug_and_archived_state_cannot_be_changed_in_the_database()
    {
        var (store, _, _) = await FreshAsync();
        await CreateAsync(store);

        await using (var rename = db.DataSource.CreateCommand("UPDATE project SET slug = 'beta'"))
            await Assert.ThrowsAsync<PostgresException>(() => rename.ExecuteNonQueryAsync(Ct));

        await store.ArchiveAsync(ProjectActor.Owner(), "alpha", Ct);
        await using var reopen = db.DataSource.CreateCommand("UPDATE project SET state = 'active'");
        await Assert.ThrowsAsync<PostgresException>(() => reopen.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public void Host_ships_the_projects_component_with_its_migration_namespace()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "projects");
        Assert.Equal(ProjectsComponent.MigrationProvider, ProjectsComponent.ManifestDefinition.MigrationNamespace);
        Assert.Equal(["audit"], ProjectsComponent.ManifestDefinition.DependsOn);

        var source = HostMigrations.Source();
        Assert.Contains("projects", source.Namespaces());
        Assert.Equal([1], source.Load("projects").Select(f => f.Version).ToList());
    }

    [Fact]
    public void Options_default_open_and_never_loosen_below_the_owner_floor()
    {
        Assert.True(ProjectsOptions.Read(Config()).LoansGrantRead);
        Assert.False(ProjectsOptions.Read(Config(("LoansGrantRead", "false"))).LoansGrantRead);
        Assert.Equal(MemberAdders.OwnerOnly, ProjectsOptions.Read(Config(("MemberAdders", "owner"))).MemberAdders);
        Assert.Throws<InvalidOperationException>(() => ProjectsOptions.Read(Config(("LoansGrantRead", "maybe"))));
        Assert.Throws<InvalidOperationException>(() => ProjectsOptions.Read(Config(("Creators", "everyone"))));
        Assert.Throws<InvalidOperationException>(() => ProjectsOptions.Read(Config(("MemberAdders", "council"))));
    }

    private static IConfigurationSection Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => "Bazaar:Projects:" + v.Key, v => (string?)v.Value)).Build().GetSection(ProjectsOptions.SectionName);
}
