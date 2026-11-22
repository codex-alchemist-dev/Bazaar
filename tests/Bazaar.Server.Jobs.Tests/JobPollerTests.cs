using System.Threading.Channels;
using Bazaar.Server.Jobs;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Jobs.Tests;

public sealed class JobPollerTests(JobDb db) : IClassFixture<JobDb>
{
    private JobStore Store => new(db.DataSource);

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs a body for one kind of job and records every job it sees.</summary>
    private sealed class Handler(string kind, Func<JobRecord, CancellationToken, Task> body) : IJobHandler
    {
        public string Kind { get; } = kind;

        public List<JobRecord> Seen { get; } = [];

        public Task HandleAsync(JobRecord job, CancellationToken ct)
        {
            Seen.Add(job);
            return body(job, ct);
        }
    }

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

    private static JobPollerOptions Immediate(int maxAttempts) => new()
    {
        MaxAttempts = maxAttempts,
        RetryBaseDelay = TimeSpan.Zero,
        RetryMaxDelay = TimeSpan.Zero,
    };

    [Fact]
    public async Task A_successful_handler_completes_its_job()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var handler = new Handler("ok", (_, _) => Task.CompletedTask);
        var poller = new JobPoller(Store, [handler], new FakeClock());
        var id = (await Store.EnqueueAsync("ok", """{"k":"v"}""", ct: Ct))!.Value;

        var claimed = await poller.PollOnceAsync(Ct);

        Assert.Equal(1, claimed);
        var job = Assert.Single(handler.Seen);
        Assert.Equal(id, job.Id);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("succeeded", (await Store.GetAsync(id, Ct))!.State);
    }

    [Theory]
    [InlineData(3650)]
    [InlineData(-3650)]
    public async Task Recurring_jobs_follow_database_time_not_a_skewed_host_clock(int skewDays)
    {
        // Chaos campaign disk-clock-resources: with the host clock ten years ahead the buckets were host-dated, so run_at sat in 2036
        // and no recurring job ran until the database caught up. Doc 03: leases and due times use database now().
        SkipWithoutDatabase();
        await DrainAsync();
        var kind = "skew-" + Guid.NewGuid().ToString("N");
        var handler = new Handler(kind, (_, _) => Task.CompletedTask);
        var scheduler = new RecurringJobScheduler(Store, [new RecurringJob(kind, TimeSpan.FromMinutes(1))]);
        var hostClock = new FakeClock(DateTimeOffset.UtcNow.AddDays(skewDays));
        var poller = new JobPoller(Store, [handler], hostClock, recurring: scheduler);

        var claimed = await poller.PollOnceAsync(Ct);

        Assert.True(claimed >= 1);
        Assert.Single(handler.Seen);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(
            long.Parse(handler.Seen[0].DedupeKey!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture))).TotalMinutes) < 5);
    }

    [Fact]
    public async Task A_throwing_handler_is_retried_then_failed_on_the_last_attempt()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var handler = new Handler("boom", (_, _) => throw new InvalidOperationException("boom"));
        var poller = new JobPoller(Store, [handler], new FakeClock(), Immediate(maxAttempts: 2));
        var id = (await Store.EnqueueAsync("boom", "{}", ct: Ct))!.Value;

        await poller.PollOnceAsync(Ct);
        var afterFirst = await Store.GetAsync(id, Ct);
        Assert.Equal("queued", afterFirst!.State);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal("InvalidOperationException: boom", afterFirst.LastError);

        await poller.PollOnceAsync(Ct);
        var afterSecond = await Store.GetAsync(id, Ct);
        Assert.Equal("failed", afterSecond!.State);
        Assert.Equal(2, afterSecond.Attempts);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task A_job_with_no_handler_is_retried_and_then_failed()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var poller = new JobPoller(Store, [], new FakeClock(), Immediate(maxAttempts: 1));
        var id = (await Store.EnqueueAsync("orphan", "{}", ct: Ct))!.Value;

        await poller.PollOnceAsync(Ct);

        var state = await Store.GetAsync(id, Ct);
        Assert.Equal("failed", state!.State);
        Assert.Contains("no job handler is registered for kind 'orphan'", state.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lost_lease_leaves_the_job_to_its_new_owner()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var id = 0L;
        var handler = new Handler("lost", async (job, _) =>
        {
            // Another host takes the job while this handler runs.
            await using var cmd = db.DataSource.CreateCommand(
                "UPDATE job SET lease_owner = 'takeover' WHERE id = @id");
            cmd.Parameters.AddWithValue("id", job.Id);
            await cmd.ExecuteNonQueryAsync(Ct);
        });
        var poller = new JobPoller(Store, [handler], new FakeClock());
        id = (await Store.EnqueueAsync("lost", "{}", ct: Ct))!.Value;

        await poller.PollOnceAsync(Ct);

        var state = await Store.GetAsync(id, Ct);
        Assert.Equal("running", state!.State);
        Assert.Equal("takeover", state.LeaseOwner);
    }

    [Fact]
    public void Two_handlers_for_one_kind_are_rejected()
    {
        // The constructor never touches the store, so no database is needed here (this runs on Windows without one).
        var ex = Assert.Throws<ArgumentException>(() => new JobPoller(
            null!,
            [new Handler("same", (_, _) => Task.CompletedTask), new Handler("same", (_, _) => Task.CompletedTask)],
            new FakeClock()));
        Assert.Contains("'same'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_loop_polls_on_the_fake_clock_and_stops_when_cancelled()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var clock = new FakeClock();
        var ran = Channel.CreateUnbounded<long>();
        var handler = new Handler("tick", (job, _) =>
        {
            ran.Writer.TryWrite(job.Id);
            return Task.CompletedTask;
        });
        var poller = new JobPoller(Store, [handler], clock, new JobPollerOptions { PollInterval = TimeSpan.FromSeconds(5) });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = (await Store.EnqueueAsync("tick", "{}", ct: Ct))!.Value;
        var loop = poller.RunAsync(cts.Token);
        Assert.Equal(first, await ran.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));

        // Nothing is due until the fake clock reaches the next tick.
        var second = (await Store.EnqueueAsync("tick", "{}", ct: Ct))!.Value;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(second, await ran.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Null(poller.LastPollError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_during_a_batch_gives_back_the_jobs_it_never_started_without_spending_their_attempts(bool handlerThrows)
    {
        // Chaos race round 2: the host claimed a batch, then stopped. The unstarted jobs kept their lease (five minutes) and
        // lost one attempt each, so a few restarts could fail jobs that had never run.
        SkipWithoutDatabase();
        await DrainAsync();
        var kind = "stop-" + Guid.NewGuid().ToString("N");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var handler = new Handler(kind, async (_, ct) =>
        {
            await cts.CancelAsync();
            if (handlerThrows)
                ct.ThrowIfCancellationRequested();
        });
        var poller = new JobPoller(Store, [handler], new FakeClock());
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
            ids.Add((await Store.EnqueueAsync(kind, "{}", ct: Ct))!.Value);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollOnceAsync(cts.Token));

        Assert.Single(handler.Seen);
        foreach (var id in ids.Skip(1))
        {
            var state = (await Store.GetAsync(id, Ct))!;
            Assert.Equal("queued", state.State);
            Assert.Equal(0, state.Attempts);
            Assert.Null(state.LeaseOwner);
        }

        var next = await Store.ClaimDueAsync("next-host", TimeSpan.FromMinutes(1), 10, Ct);
        Assert.Equal(ids.Skip(1).Order(), next.Where(j => j.Kind == kind).Select(j => j.Id).Order());
    }
}
