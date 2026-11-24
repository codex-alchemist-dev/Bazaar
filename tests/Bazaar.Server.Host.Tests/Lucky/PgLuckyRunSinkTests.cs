using Bazaar.Lucky;
using Bazaar.Sanitization;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Host.Lucky;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.Server.Sanitization;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Host.Tests.Lucky;

/// <summary>A cloned test database migrated with the real schema, plus the Postgres Lucky sink wired to real services.</summary>
public sealed class LuckyDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("LuckyDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 8 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }

    public new async ValueTask DisposeAsync()
    {
        if (_pool is not null)
            await _pool.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>Drops GC notifications: the sink test checks rows, not fan-out.</summary>
internal sealed class SilentNotifier : IGcNotifier
{
    public void Notify(Guid gcId, long seq) { }
}

public sealed class PgLuckyRunSinkTests(LuckyDb db) : IClassFixture<LuckyDb>
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], [], [], [], []);
    private static readonly Guid _owner = Guid.Parse("0196f0e0-0000-7000-8000-00000000b001");
    private static readonly Guid _agentB = Guid.Parse("0196f0e0-0000-7000-8000-00000000a0b2");
    private static readonly Guid _agentC = Guid.Parse("0196f0e0-0000-7000-8000-00000000a0c3");
    private static readonly Guid _project = Guid.Parse("0196f0e0-0000-7000-8000-00000000cafe");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PgLuckyRunSink BuildSink()
    {
        var notifier = new SilentNotifier();
        var log = new GcLogWriter(db.DataSource, notifier);
        return new PgLuckyRunSink(
            db.DataSource,
            new GcCreateService(db.DataSource),
            log,
            new PlaintextGcBodyCodec(),
            new InboxRepository(db.DataSource),
            new TaskService(db.DataSource, gcLog: log));
    }

    private async Task SeedAgentsAsync()
    {
        foreach (var (id, name) in new[] { (_agentB, "lucky b"), (_agentC, "lucky c") })
        {
            await using var cmd = db.DataSource.CreateCommand(
                "INSERT INTO agent (agent_id, name, state) VALUES ($1, $2, 'active') ON CONFLICT DO NOTHING");
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    private static LuckyRunPlan BuildPlan(string runId)
    {
        var controller = new LuckyController(new LuckySettings(), new TextSanitizer());
        var candidates = new[]
        {
            new LuckyCandidate(_agentB.ToString(), OnShift: false, OptedIn: true),
            new LuckyCandidate(_agentC.ToString(), OnShift: false, OptedIn: true),
        };
        var run = controller.Start(runId, "stuck on the cache", true, _start, 0, candidates, () => 7).Run!;
        run = controller.RecordIdea(run, _agentB.ToString(), "cache per branch", _start.AddMinutes(1), _terms).Run!;
        run = controller.RecordIdea(run, _agentC.ToString(), "shard the index", _start.AddMinutes(2), _terms).Run!;
        run = controller.MarkForCard(run, 0, markedByOwner: true).Run!;
        return controller.Plan(run);
    }

    [Fact]
    public async Task Applying_a_plan_twice_writes_each_step_once()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        await SeedAgentsAsync();
        var plan = BuildPlan(Guid.CreateVersion7().ToString("N"));
        var sink = BuildSink();

        var first = await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, Ct);
        var second = await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, Ct);

        Assert.Equal(first.GcId, second.GcId);
        Assert.Equal(first.CardIds, second.CardIds);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM gc WHERE gc_id = $1", first.GcId));
        // One opening message and two ideas, however many times the plan is applied.
        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM gc_message WHERE gc_id = $1", first.GcId));
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM inbox_item WHERE gc_id = $1 AND kind = 'brainstorm'", first.GcId));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM tasks WHERE origin_gc_id = $1", first.GcId));
    }

    [Fact]
    public async Task Only_opted_in_agents_are_woken_and_only_kept_owner_marked_ideas_become_cards()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        await SeedAgentsAsync();
        var plan = BuildPlan(Guid.CreateVersion7().ToString("N"));
        var sink = BuildSink();

        var result = await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, Ct);

        Assert.Equal(2, result.WakesInserted);
        Assert.Single(result.CardIds);
        Assert.Equal("cache per branch", await ScalarAsync<string>("SELECT title FROM tasks WHERE id = $1", result.CardIds[0]));
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
