using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Reputation.Tests;

/// <summary>A throwaway database with the ledger tables the watcher and projection read (columns as in common/0013).</summary>
public sealed class LedgerDb : IAsyncLifetime
{
    private PgSource? _source;
    private string _database = "";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _source = await PgSource.GetSharedAsync();
        _database = "ledger_" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(_source.AdminConnectionString))
        {
            await using var create = admin.CreateCommand($"CREATE DATABASE \"{_database}\"");
            await create.ExecuteNonQueryAsync();
        }

        DataSource = NpgsqlDataSource.Create(_source.ConnectionStringFor(_database));
        await using var tables = DataSource.CreateCommand("""
            CREATE TABLE outcome_event (
                id uuid PRIMARY KEY, kind text NOT NULL, pr_ref text NOT NULL, x numeric NOT NULL,
                w numeric NOT NULL, dedupe_key text NOT NULL UNIQUE, source_ref text, occurred_at timestamptz NOT NULL,
                recorded_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE outcome_attribution (
                outcome_id uuid NOT NULL REFERENCES outcome_event (id), agent_id text NOT NULL, role text NOT NULL,
                tag text NOT NULL, w numeric NOT NULL, PRIMARY KEY (outcome_id, agent_id, tag));
            CREATE TABLE review (
                id uuid PRIMARY KEY, pr_ref text NOT NULL, reviewer_agent_id text NOT NULL, author_agent_id text NOT NULL, tag text NOT NULL,
                overall smallint NOT NULL, supersedes_id uuid, created_at timestamptz NOT NULL DEFAULT now());
            """);
        await tables.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(_source!.AdminConnectionString);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    public async Task AddOutcomeAsync(Guid agent, string tag, decimal x, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("""
            INSERT INTO outcome_event (id, kind, pr_ref, x, w, dedupe_key, occurred_at) VALUES (@id, 'ci_first_run', 'pr', @x, 1, @key, @at);
            INSERT INTO outcome_attribution (outcome_id, agent_id, role, tag, w) VALUES (@id, @agent, 'author', @tag, 1);
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("x", x);
        cmd.Parameters.AddWithValue("key", id.ToString("N"));
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("agent", agent.ToString());
        cmd.Parameters.AddWithValue("tag", tag);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class OutcomeWardWatcherTests(LedgerDb db) : IClassFixture<LedgerDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class CapturingIntake(bool throws = false) : IWardTriggerIntake
    {
        public List<(Guid Agent, string Tag, bool[] Window)> Reports { get; } = [];

        public Task<WardOutcome?> ReportOutcomesAsync(Guid agentId, string tag, IReadOnlyList<bool> recent, CancellationToken ct)
        {
            Reports.Add((agentId, tag, [.. recent]));
            return throws
                ? throw new InvalidOperationException("ward down")
                : Task.FromResult<WardOutcome?>(WardOutcome.Accept(new WardStay(Guid.NewGuid(), agentId, WardTrigger.RepeatedFailure, null, _t0)));
        }

        public Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_newest_outcomes_on_the_tag_are_handed_over_newest_first_with_failures_as_true()
    {
        var agent = Guid.CreateVersion7();
        await db.AddOutcomeAsync(agent, "csharp", 1m, _t0);
        await db.AddOutcomeAsync(agent, "csharp", 0m, _t0.AddHours(1));
        await db.AddOutcomeAsync(agent, "csharp", 0m, _t0.AddHours(2));
        await db.AddOutcomeAsync(agent, "csharp", 0.25m, _t0.AddHours(3));
        await db.AddOutcomeAsync(agent, "docs", 0m, _t0.AddHours(4));
        var intake = new CapturingIntake();

        var admitted = await new OutcomeWardWatcher(db.DataSource, intake, new OutcomeWardWatcherOptions()).CheckAsync(agent.ToString(), "csharp", TestContext.Current.CancellationToken);

        Assert.True(admitted);
        var report = Assert.Single(intake.Reports);
        Assert.Equal("csharp", report.Tag);
        Assert.Equal([false, true, true, false], report.Window);
    }

    [Fact]
    public async Task The_window_size_limits_what_is_read()
    {
        var agent = Guid.CreateVersion7();
        for (var i = 0; i < 5; i++)
        {
            await db.AddOutcomeAsync(agent, "t", 0m, _t0.AddHours(i));
        }

        var intake = new CapturingIntake();
        await new OutcomeWardWatcher(db.DataSource, intake, new OutcomeWardWatcherOptions { WindowSize = 3 }).CheckAsync(agent.ToString(), "t", TestContext.Current.CancellationToken);

        Assert.Equal(3, Assert.Single(intake.Reports).Window.Length);
    }

    [Fact]
    public async Task A_failing_ward_never_breaks_the_caller()
    {
        var agent = Guid.CreateVersion7();
        await db.AddOutcomeAsync(agent, "t", 0m, _t0);

        var admitted = await new OutcomeWardWatcher(db.DataSource, new CapturingIntake(throws: true), new OutcomeWardWatcherOptions()).CheckAsync(agent.ToString(), "t", TestContext.Current.CancellationToken);

        Assert.False(admitted);
    }

    [Fact]
    public async Task A_non_guid_agent_is_not_reported()
    {
        var intake = new CapturingIntake();

        var admitted = await new OutcomeWardWatcher(db.DataSource, intake, new OutcomeWardWatcherOptions()).CheckAsync("agent-a", "t", TestContext.Current.CancellationToken);

        Assert.False(admitted);
        Assert.Empty(intake.Reports);
    }
}
