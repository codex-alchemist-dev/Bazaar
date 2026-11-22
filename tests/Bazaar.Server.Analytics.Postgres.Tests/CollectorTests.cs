using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Analytics.Postgres.Tests;

/// <summary>A cloned test database migrated with the core schema, which holds the Kanban (0012) and reputation ledger (0013) tables.</summary>
public sealed class AnalyticsDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        await base.InitializeAsync();
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }
}

/// <summary>The collectors read real Kanban and reputation rows on PostgreSQL and produce the right work events.</summary>
public sealed class CollectorTests(AnalyticsDb db) : IClassFixture<AnalyticsDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private static readonly AnalyticsWindow _window = new(_now.AddDays(-7), _now.AddMinutes(1));

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> TaskAsync(Guid agent, params (string Kind, string Payload, TimeSpan Ago)[] events)
    {
        var gc = Guid.CreateVersion7();
        await Exec("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES (@a, 'general', 'test', @b)", ("a", gc), ("b", Guid.CreateVersion7()));
        var id = Guid.CreateVersion7();
        await Exec("INSERT INTO tasks (id, project_id, title, state, owner_agent_id, origin_gc_id) VALUES (@id, @p, 'T', 'done', @o, @gc)",
            ("id", id), ("p", Guid.CreateVersion7()), ("o", agent), ("gc", gc));
        foreach (var (kind, payload, ago) in events)
        {
            await using var cmd = db.DataSource.CreateCommand("INSERT INTO task_events (task_id, kind, actor, payload, at) VALUES (@t, @k, 'system', @p::jsonb, @at)");
            cmd.Parameters.AddWithValue("t", id);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("p", payload);
            cmd.Parameters.AddWithValue("at", _now - ago);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        return id;
    }

    private async Task Exec(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Kanban_events_become_completed_review_and_merge_events_for_the_owner_agent()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        var withPr = await TaskAsync(agent,
            ("task.transitioned", """{"from":"in_progress","to":"review"}""", TimeSpan.FromDays(3)),
            ("task.review_approved", "{}", TimeSpan.FromDays(2)),
            ("task.transitioned", """{"from":"review","to":"done"}""", TimeSpan.FromDays(1)));
        await Exec("INSERT INTO task_links (task_id, kind, ref) VALUES (@t, 'pr', 'o/r#1')", ("t", withPr));
        await TaskAsync(agent, ("task.transitioned", """{"from":"review","to":"in_progress"}""", TimeSpan.FromDays(1)));
        await TaskAsync(agent, ("task.transitioned", """{"from":"ready","to":"done"}""", TimeSpan.FromDays(30))); // outside the window

        var events = (await new KanbanWorkEventCollector(db.DataSource).CollectAsync(_window, Ct)).Where(e => e.AgentId == agent.ToString()).ToArray();

        Assert.Equal(1, events.Count(e => e.Kind == AgentWorkEventKind.TaskCompleted));
        Assert.Equal(1, events.Count(e => e.Kind == AgentWorkEventKind.PrMerged));
        Assert.Equal(1, events.Count(e => e.Kind == AgentWorkEventKind.ReviewPassed));
        Assert.Equal(1, events.Count(e => e.Kind == AgentWorkEventKind.ReviewChangesRequested));
        Assert.Equal(4, events.Length);
    }

    [Fact]
    public async Task Reverts_are_credited_to_the_author_role_only_and_once_per_outcome()
    {
        SkipWithoutDatabase();
        var author = "agent-" + Guid.NewGuid().ToString("N");
        var reviewer = "agent-" + Guid.NewGuid().ToString("N");
        var outcome = Guid.CreateVersion7();
        await Exec("INSERT INTO outcome_event (id, kind, pr_ref, x, w, dedupe_key, occurred_at) VALUES (@id, 'revert', 'o/r#2', 0, 1, @k, @at)",
            ("id", outcome), ("k", Guid.NewGuid().ToString()), ("at", _now.AddDays(-1)));
        await Exec("INSERT INTO outcome_attribution (outcome_id, agent_id, role, tag, w) VALUES (@o, @a, 'author', 'csharp', 1), (@o, @a, 'author', 'docs', 1), (@o, @r, 'reviewer', 'csharp', 1)",
            ("o", outcome), ("a", author), ("r", reviewer));

        var events = await new RevertWorkEventCollector(db.DataSource).CollectAsync(_window, Ct);

        Assert.Single(events, e => e.AgentId == author);
        Assert.DoesNotContain(events, e => e.AgentId == reviewer);
        Assert.All(events, e => Assert.Equal(AgentWorkEventKind.PrReverted, e.Kind));
    }
}
