using System.Text.Json;
using Bazaar.Server.Jobs;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Jobs;

public sealed class JobStoreTests(JobDb db) : IClassFixture<JobDb>
{
    private JobStore Store => new(db.DataSource);

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Completes every due job, so each test starts from an empty queue (tests in a class share one database).</summary>
    private async Task DrainAsync()
    {
        while (true)
        {
            var batch = await Store.ClaimDueAsync("drain", TimeSpan.FromMinutes(1), 100, Ct);
            if (batch.Count == 0)
                return;
            foreach (var job in batch)
                await Store.CompleteAsync(job.Id, "drain", Ct);
        }
    }

    private async Task ExecAsync(string sql, long id)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Enqueue_returns_an_id_and_keeps_the_payload()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var id = await Store.EnqueueAsync("demo", """{"n":1}""", ct: Ct);

        Assert.NotNull(id);
        var job = Assert.Single(await Store.ClaimDueAsync("owner-a", TimeSpan.FromMinutes(1), 10, Ct));
        Assert.Equal(id, job.Id);
        Assert.Equal("demo", job.Kind);
        Assert.Equal(1, job.Attempts);
        using var payload = JsonDocument.Parse(job.PayloadJson);
        Assert.Equal(1, payload.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task A_repeated_dedupe_key_is_ignored_and_the_first_job_wins()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var first = await Store.EnqueueAsync("dedupe", "{}", dedupeKey: "same-key", ct: Ct);
        var second = await Store.EnqueueAsync("dedupe", "{}", dedupeKey: "same-key", ct: Ct);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task Jobs_in_the_future_are_not_claimed_until_due()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        await Store.EnqueueAsync("later", "{}", runAt: DateTimeOffset.UtcNow.AddDays(1), ct: Ct);

        Assert.Empty(await Store.ClaimDueAsync("owner-a", TimeSpan.FromMinutes(1), 10, Ct));
    }

    [Fact]
    public async Task Claims_are_oldest_first_and_bounded_by_the_batch_size()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
            ids.Add((await Store.EnqueueAsync("batch", "{}", ct: Ct))!.Value);

        var claimed = await Store.ClaimDueAsync("owner-b", TimeSpan.FromMinutes(1), 3, Ct);

        Assert.Equal(ids.Take(3), claimed.Select(j => j.Id));
    }

    [Fact]
    public async Task Concurrent_claimers_never_take_the_same_job()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        for (var i = 0; i < 40; i++)
            await Store.EnqueueAsync("race", "{}", ct: Ct);

        var batches = await Task.WhenAll(Enumerable.Range(0, 8).Select(n =>
            Store.ClaimDueAsync($"racer-{n}", TimeSpan.FromMinutes(1), 40, Ct)));

        var all = batches.SelectMany(b => b.Select(j => j.Id)).ToList();
        Assert.Equal(40, all.Count);
        Assert.Equal(40, all.Distinct().Count());
    }

    [Fact]
    public async Task An_expired_lease_is_claimable_again_and_counts_another_attempt()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var id = (await Store.EnqueueAsync("lease", "{}", ct: Ct))!.Value;
        Assert.Single(await Store.ClaimDueAsync("crashed-host", TimeSpan.FromMinutes(1), 10, Ct));

        // The lease runs out on the database clock; move it into the past rather than sleeping.
        await ExecAsync("UPDATE job SET lease_until = now() - interval '1 second' WHERE id = @id", id);

        var reclaimed = Assert.Single(await Store.ClaimDueAsync("new-host", TimeSpan.FromMinutes(1), 10, Ct));
        Assert.Equal(id, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempts);
    }

    [Fact]
    public async Task Only_the_lease_holder_can_complete_a_job()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var id = (await Store.EnqueueAsync("complete", "{}", ct: Ct))!.Value;
        await Store.ClaimDueAsync("holder", TimeSpan.FromMinutes(1), 10, Ct);

        Assert.False(await Store.CompleteAsync(id, "someone-else", Ct));
        Assert.True(await Store.CompleteAsync(id, "holder", Ct));

        var state = await Store.GetAsync(id, Ct);
        Assert.Equal("succeeded", state!.State);
        Assert.Null(state.LeaseOwner);
    }

    [Fact]
    public async Task A_failure_requeues_later_and_the_last_attempt_fails_the_job()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var id = (await Store.EnqueueAsync("fail", "{}", ct: Ct))!.Value;

        await Store.ClaimDueAsync("holder", TimeSpan.FromMinutes(1), 10, Ct);
        Assert.True(await Store.FailAsync(id, "holder", 2, TimeSpan.FromMinutes(10), "boom", Ct));
        var queued = await Store.GetAsync(id, Ct);
        Assert.Equal("queued", queued!.State);
        Assert.Equal("boom", queued.LastError);
        Assert.True(queued.RunAt > DateTimeOffset.UtcNow.AddMinutes(5));

        // Make the job due again so the second attempt is the last one.
        await ExecAsync("UPDATE job SET run_at = now() WHERE id = @id", id);
        await Store.ClaimDueAsync("holder", TimeSpan.FromMinutes(1), 10, Ct);
        Assert.True(await Store.FailAsync(id, "holder", 2, TimeSpan.FromMinutes(10), "boom again", Ct));

        var failed = await Store.GetAsync(id, Ct);
        Assert.Equal("failed", failed!.State);
        Assert.Equal(2, failed.Attempts);
    }

    [Fact]
    public async Task A_lease_that_expired_on_its_final_attempt_is_failed_not_retried()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var id = (await Store.EnqueueAsync("exhaust", "{}", ct: Ct))!.Value;
        await Store.ClaimDueAsync("crashing", TimeSpan.FromMinutes(1), 10, Ct);
        await ExecAsync("UPDATE job SET lease_until = now() - interval '1 second', attempts = 3 WHERE id = @id", id);

        Assert.Equal(1, await Store.FailExhaustedAsync(3, Ct));
        Assert.Equal("failed", (await Store.GetAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task The_schema_rejects_a_running_job_without_a_lease()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO job (kind, run_at, state, payload) VALUES ('bad', now(), 'running', '{}')");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Equal("23514", ex.SqlState);
    }
}
