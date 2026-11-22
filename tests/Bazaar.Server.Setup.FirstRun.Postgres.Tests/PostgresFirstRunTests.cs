using Bazaar.Server.Agents.Data;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup.FirstRun.Gateway;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

internal sealed class MemoryPolicyStore : IDefaultAgentPolicyStore
{
    public Dictionary<string, (IReadOnlyList<string> Entries, string? Default, string By)> Rows { get; } = [];

    public Task<string?> GetChangedByAsync(string agentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.TryGetValue(agentId, out var r) ? r.By : null);

    public Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken = default)
    {
        Rows[agentId] = (entries, defaultRoute, changedBy);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

internal sealed class FixedCommit(string? sha) : ICurrentConstitutionCommit
{
    public Task<string?> GetCurrentCommitShaAsync(CancellationToken cancellationToken = default) => Task.FromResult(sha);
}

public sealed class PostgresFirstRunTests(FirstRunDb db) : IClassFixture<FirstRunDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Whitelist = ["default-agent-balanced", "a:claude-x"];

    private static void NeedDb() => Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private (PostgresFirstRunStore Store, MemoryPolicyStore Policies) NewStore()
    {
        var policies = new MemoryPolicyStore();
        var store = new PostgresFirstRunStore(db.DataSource, new PgAgentFirstRunSeeds(db.DataSource), new DefaultAgentPolicyGrant(policies));
        return (store, policies);
    }

    [Fact]
    public async Task Owner_is_the_active_owner_row_or_null()
    {
        NeedDb();
        var owner = new PostgresFirstRunOwner(db.DataSource);
        var existing = await owner.GetOwnerPrincipalIdAsync(Ct);
        if (existing is null)
        {
            var id = await db.InsertOwnerAsync();
            Assert.Equal(id, await owner.GetOwnerPrincipalIdAsync(Ct));
        }
        else
        {
            Assert.NotNull(existing);
        }
    }

    [Fact]
    public async Task General_gc_is_created_once_with_the_owner_as_member()
    {
        NeedDb();
        var (store, _) = NewStore();
        var ownerId = Guid.CreateVersion7();

        var first = await store.EnsureGeneralGcAsync("General", ownerId, Ct);
        var second = await store.EnsureGeneralGcAsync("General", ownerId, Ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.GcId, second.GcId);
        Assert.Equal("general", await db.ScalarAsync<string>("SELECT type_key FROM gc WHERE gc_id = $1", first.GcId));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_member WHERE gc_id = $1 AND principal_id = $2", first.GcId, ownerId));
    }

    [Fact]
    public async Task Default_agent_is_held_in_the_gc_with_its_policy_and_is_found_again()
    {
        NeedDb();
        var (store, policies) = NewStore();
        var gc = await store.EnsureGeneralGcAsync("General", Guid.CreateVersion7(), Ct);

        var agent = await store.EnsureDefaultAgentAsync("Claude", "", gc.GcId, Whitelist, Ct);
        var again = await store.EnsureDefaultAgentAsync("Claude", "", gc.GcId, Whitelist, Ct);

        Assert.True(agent.Created);
        Assert.False(again.Created);
        Assert.Equal(agent.AgentId, again.AgentId);
        Assert.Equal("created", await db.ScalarAsync<string>("SELECT state FROM agent WHERE agent_id = $1", agent.AgentId));
        Assert.True(await db.ScalarAsync<bool>("SELECT first_run_hold FROM agent WHERE agent_id = $1", agent.AgentId));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_member WHERE gc_id = $1 AND principal_id = $2", gc.GcId, agent.AgentId));
        Assert.Equal(Whitelist, policies.Rows[agent.AgentId.ToString("D")].Entries);
    }

    [Fact]
    public async Task Empty_whitelist_creates_the_agent_without_a_policy()
    {
        NeedDb();
        var db2 = new MemoryPolicyStore();
        var store = new PostgresFirstRunStore(db.DataSource, new PgAgentFirstRunSeeds(db.DataSource), new DefaultAgentPolicyGrant(db2));
        // The marker is shared by the class's database, so remove any earlier seeded agent first.
        var seeds = new PgAgentFirstRunSeeds(db.DataSource);
        if (await seeds.FindByMarkerAsync(FirstRunMarkers.DefaultAgent, Ct) is { } old) await seeds.RemoveUnstartedAsync(old, Ct);

        var agent = await store.EnsureDefaultAgentAsync("Claude", "", null, [], Ct);

        Assert.True(agent.Created);
        Assert.Empty(db2.Rows);
    }

    [Fact]
    public async Task Activate_releases_the_hold_and_starts_onboarding_and_remove_deletes_an_unstarted_agent()
    {
        NeedDb();
        var (store, _) = NewStore();
        var seeds = new PgAgentFirstRunSeeds(db.DataSource);
        if (await seeds.FindByMarkerAsync(FirstRunMarkers.DefaultAgent, Ct) is { } old) await seeds.RemoveUnstartedAsync(old, Ct);
        var agent = await store.EnsureDefaultAgentAsync("Claude", "", null, Whitelist, Ct);

        await store.ActivateAgentAsync(agent.AgentId, Ct);
        Assert.Equal("onboarding", await db.ScalarAsync<string>("SELECT state FROM agent WHERE agent_id = $1", agent.AgentId));

        await store.RemoveAgentAsync(agent.AgentId, Ct);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM agent WHERE agent_id = $1", agent.AgentId));
    }

    [Fact]
    public async Task Archive_marks_the_gc_archived()
    {
        NeedDb();
        var (store, _) = NewStore();
        var gc = await store.EnsureGeneralGcAsync("General", Guid.CreateVersion7(), Ct);

        await store.ArchiveGcAsync(gc.GcId, Ct);

        Assert.True(await db.ScalarAsync<bool>("SELECT archived_at IS NOT NULL FROM gc WHERE gc_id = $1", gc.GcId));
    }

    [Fact]
    public async Task Acceptance_needs_a_current_commit_and_is_idempotent()
    {
        NeedDb();
        var owner = Guid.CreateVersion7();
        var none = new PostgresConstitutionAcceptance(db.DataSource, new NoCurrentConstitutionCommit());
        Assert.False(await none.RecordOwnerAcceptanceAsync(owner, Ct));

        var sha = new string('a', 40);
        var accept = new PostgresConstitutionAcceptance(db.DataSource, new FixedCommit(sha));
        Assert.True(await accept.RecordOwnerAcceptanceAsync(owner, Ct));
        Assert.True(await accept.RecordOwnerAcceptanceAsync(owner, Ct));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM firstrun_constitution_acceptance WHERE owner_id = $1", owner));
    }

    [Fact]
    public async Task Audit_row_holds_ids_and_flags_only()
    {
        var log = new MemoryAudit();
        var gc = Guid.NewGuid();
        await new AuditLogFirstRunAudit(log).WriteSeededAsync(gc, null, true, "operator", Ct);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("setup.firstrun.seeded", entry.Kind);
        Assert.Equal("operator", entry.Actor);
        Assert.Equal($"{{\"gcId\":\"{gc:D}\",\"agentId\":null,\"constitutionAccepted\":true}}", entry.Payload);
    }
}
