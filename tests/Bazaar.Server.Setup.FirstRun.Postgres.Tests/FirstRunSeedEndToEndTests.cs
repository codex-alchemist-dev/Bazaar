using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>The seed step over the real stores: the whole path the host runs. Needs PostgreSQL.</summary>
public sealed class FirstRunSeedEndToEndTests
{
    private const string _commit = "0123456789abcdef0123456789abcdef01234567";
    private const string _canary = "canary-purpose-9f3a";

    private static SetupApplyContext Context(string hash) => new(hash, SetupEntryPath.Api, "setup:bootstrap", null, null, SetupStateSnapshot.Empty, []);

    private static SetupPlan Plan(bool accept) => new()
    {
        FirstRun = new SetupFirstRunSection { AcceptConstitution = accept, DefaultAgentPurpose = _canary },
    };

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private static FirstRunSeedStep Step(Npgsql.NpgsqlDataSource ds, RecordingAudit audit) => new(
        new PostgresFirstRunOwner(ds), new PostgresFirstRunStore(ds), new PostgresConstitutionAcceptance(ds), new PostgresDefaultAgentRoutes(ds), new AuditLogFirstRunAudit(audit));

    [Fact]
    public async Task Accepting_the_constitution_activates_the_agent_and_a_second_apply_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        await FirstRunDatabase.AddModelAsync(db.DataSource, "anthropic-direct", "claude-haiku-5-5", ct);
        await using (var vault = db.DataSource.CreateCommand($"INSERT INTO vault_docs (type, head_commit) VALUES ('constitution', '{_commit}')"))
            await vault.ExecuteNonQueryAsync(ct);
        var audit = new RecordingAudit();
        var step = Step(db.DataSource, audit);

        var first = await step.ApplyAsync(Plan(accept: true), Context("h1"), ct);
        var second = await step.ApplyAsync(Plan(accept: true), Context("h2"), ct);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM gc", ct));
        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM agent", ct));
        Assert.Equal("active", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT state FROM agent", ct));
        Assert.Equal(2, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM gc_member", ct));
        Assert.All(audit.Entries, entry => Assert.Equal("setup.firstrun.seeded", entry.Kind));
        Assert.DoesNotContain(audit.Entries, entry => entry.Payload.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(first.Message ?? "", _canary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_acceptance_the_agent_waits_and_without_an_owner_the_step_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var audit = new RecordingAudit();
        var step = Step(db.DataSource, audit);

        var noOwner = await step.ApplyAsync(Plan(accept: false), Context("h1"), ct);
        await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        var waiting = await step.ApplyAsync(Plan(accept: false), Context("h2"), ct);

        Assert.False(noOwner.Succeeded);
        Assert.True(waiting.Succeeded);
        Assert.Equal("created", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT state FROM agent", ct));
        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM agent_model_policy", ct));
    }

    [Fact]
    public async Task Rolling_back_removes_the_agent_and_archives_the_gc()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        var step = Step(db.DataSource, new RecordingAudit());
        var plan = Plan(accept: false);

        await step.ApplyAsync(plan, Context("h1"), ct);
        await step.RollbackAsync(plan, Context("h1"), ct);

        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM agent", ct));
        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM gc WHERE archived_at IS NOT NULL", ct));
    }
}
