using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingStore.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.CodingStore.Tests;

public sealed class ToolCallStoreTests(CodingDb db) : IClassFixture<CodingDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private static ToolCallRecord Row(string session, ToolCallStatus status = ToolCallStatus.Unknown, DateTimeOffset? at = null) => new(
        Guid.NewGuid(), session, "exec.run", "Exec", "agent", true, status, null, new string('a', 64), at ?? _t0,
        status == ToolCallStatus.Refused ? at ?? _t0 : null);

    private PostgresToolCallStore Store() => new(db.DataSource);

    private static void RequireDb() => Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Append_then_get_round_trips_every_column()
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid(), ToolCallStatus.Refused);
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);

        var back = await Store().GetAsync(row.Id, TestContext.Current.CancellationToken);

        Assert.Equal(row, back);
    }

    [Fact]
    public async Task List_by_session_returns_only_that_session_in_start_order()
    {
        RequireDb();
        var s = "s-" + Guid.NewGuid();
        var late = Row(s, at: _t0.AddMinutes(2));
        var early = Row(s, at: _t0);
        await Store().AppendAsync(late, TestContext.Current.CancellationToken);
        await Store().AppendAsync(early, TestContext.Current.CancellationToken);
        await Store().AppendAsync(Row("other-" + Guid.NewGuid()), TestContext.Current.CancellationToken);

        var rows = await Store().ListBySessionAsync(s, TestContext.Current.CancellationToken);

        Assert.Equal([early.Id, late.Id], rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Complete_sets_the_outcome_once()
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid());
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);

        Assert.True(await Store().CompleteAsync(row.Id, ToolCallStatus.Failed, 2, TestContext.Current.CancellationToken));
        Assert.False(await Store().CompleteAsync(row.Id, ToolCallStatus.Succeeded, 0, TestContext.Current.CancellationToken));

        var back = await Store().GetAsync(row.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ToolCallStatus.Failed, back!.Status);
        Assert.Equal(2, back.ExitCode);
        Assert.NotNull(back.EndedAt);
    }

    [Fact]
    public async Task Complete_of_a_missing_row_returns_false_and_unknown_is_not_an_outcome()
    {
        RequireDb();
        Assert.False(await Store().CompleteAsync(Guid.NewGuid(), ToolCallStatus.Succeeded, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Store().CompleteAsync(Guid.NewGuid(), ToolCallStatus.Unknown, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_row_cannot_be_completed()
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid(), ToolCallStatus.Refused);
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);

        Assert.False(await Store().CompleteAsync(row.Id, ToolCallStatus.Succeeded, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_is_rejected()
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid());
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("DELETE FROM tool_calls WHERE id = $1", row.Id));
    }

    [Fact]
    public async Task Truncate_is_rejected()
    {
        RequireDb();
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("TRUNCATE tool_calls"));
    }

    [Theory]
    [InlineData("session_id = 'x'")]
    [InlineData("tool = 'x'")]
    [InlineData("capability_class = 'Read'")]
    [InlineData("source_trust = 'owner'")]
    [InlineData("tainted_at_write = false")]
    [InlineData("args_sha256 = 'x'")]
    [InlineData("started_at = started_at + interval '1 second'")]
    [InlineData("id = gen_random_uuid()")]
    public async Task Update_of_an_immutable_column_is_rejected_even_while_unknown(string set)
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid());
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync($"UPDATE tool_calls SET {set} WHERE id = $1", row.Id));
    }

    [Fact]
    public async Task A_second_outcome_is_rejected_by_the_table_itself()
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid());
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);
        await Store().CompleteAsync(row.Id, ToolCallStatus.Succeeded, 0, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecAsync("UPDATE tool_calls SET status = 'failed', exit_code = 1 WHERE id = $1", row.Id));
    }

    [Fact]
    public async Task Duplicate_id_and_bad_status_are_rejected()
    {
        RequireDb();
        var row = Row("s-" + Guid.NewGuid());
        await Store().AppendAsync(row, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PostgresException>(async () => await Store().AppendAsync(row, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(
            "INSERT INTO tool_calls VALUES (gen_random_uuid(), 's', 't', 'Exec', 'agent', false, 'bogus', null, 'h', now(), null)"));
    }
}
