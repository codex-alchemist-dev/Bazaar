using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Calendar;

/// <summary>Migration 0017 against a real PostgreSQL: it is recorded, re-applies as a no-op, and its constraints hold.</summary>
public sealed class CalendarMigrationTests(CalendarDb db) : IClassFixture<CalendarDb>
{
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Inserts a row with the given overrides. Defaults make a valid proposed vacation for <paramref name="agentId"/>.</summary>
    private Task InsertAsync(
        Guid id,
        string kind = "vacation",
        DateTimeOffset? starts = null,
        DateTimeOffset? ends = null,
        Guid? agentId = null,
        bool affects = true,
        string status = "proposed",
        long version = 1,
        byte[]? titleCt = null,
        Guid? sourceGcId = null)
    {
        const string sql = """
            INSERT INTO calendar_event (id, kind, title_key_id, title_ct, starts_at, ends_at, tz, agent_id,
                                        affects_availability, status, source_gc_id, version)
            VALUES ($1, $2, $3, $4, $5, $6, 'UTC', $7, $8, $9, $10, $11)
            """;
        return db.ExecAsync(
            sql,
            id,
            kind,
            Guid.Empty,
            titleCt ?? new byte[] { 1, 2, 3 },
            starts ?? _start,
            ends ?? _start.AddDays(2),
            agentId,
            affects,
            status,
            sourceGcId,
            version);
    }

    private static async Task<string> SqlStateAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.SqlState;
    }

    [Fact]
    public async Task Migration_0017_is_recorded_once()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var count = await db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations WHERE provider = 'common' AND version = 17");
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Reapplying_the_migrations_is_a_no_op()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var source = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);
        var result = await new MigrationRunner(db.DataSource, new MigrationOptions(16))
            .MigrateAsync(source, "linux", TestContext.Current.CancellationToken);

        Assert.Empty(result.Applied);
        Assert.True(result.AlreadyApplied > 0);
    }

    [Fact]
    public async Task An_event_must_end_after_it_starts()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = await db.CreateAgentAsync("range-agent");
        var state = await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, ends: _start, starts: _start));
        Assert.Equal("23514", state);

        var inverted = await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, starts: _start, ends: _start.AddHours(-1)));
        Assert.Equal("23514", inverted);
    }

    [Fact]
    public async Task A_vacation_must_block_availability_and_name_an_agent()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = await db.CreateAgentAsync("shape-agent");
        Assert.Equal("23514", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, affects: false)));
        Assert.Equal("23514", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: null)));
    }

    [Fact]
    public async Task A_non_vacation_may_be_informational_and_company_wide()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var id = Guid.CreateVersion7();
        await InsertAsync(id, kind: "release", agentId: null, affects: false, status: "confirmed");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM calendar_event WHERE id = $1", id));
    }

    [Fact]
    public async Task Kind_status_version_and_ciphertext_are_checked()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = await db.CreateAgentAsync("check-agent");
        Assert.Equal("23514", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), kind: "meeting", agentId: agent)));
        Assert.Equal("23514", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, status: "deleted")));
        Assert.Equal("23514", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, version: 0)));
        Assert.Equal("23514", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, titleCt: [])));
    }

    [Fact]
    public async Task Agent_and_source_gc_references_must_exist()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        Assert.Equal("23503", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: Guid.CreateVersion7())));

        var agent = await db.CreateAgentAsync("fk-agent");
        Assert.Equal("23503", await SqlStateAsync(() => InsertAsync(Guid.CreateVersion7(), agentId: agent, sourceGcId: Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Event_ids_are_unique()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = await db.CreateAgentAsync("pk-agent");
        var id = Guid.CreateVersion7();
        await InsertAsync(id, agentId: agent);
        Assert.Equal("23505", await SqlStateAsync(() => InsertAsync(id, agentId: agent)));
    }
}
