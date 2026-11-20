using System.Threading.Channels;
using Bazaar.Server.Jobs;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Jobs;

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


    // The lease is long, so real elapsed time never matters: the tests move the fake clock to fire the renewal timer and
    // move lease times in the database directly, and wait on conditions (with a generous timeout) instead of sleeping.
    private static readonly TimeSpan _lease = TimeSpan.FromHours(1);

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private async Task SqlAsync(string sql, long id)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<bool> LeaseIsLiveAsync(long id)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT lease_until > now() FROM job WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Fires the poller's renewal timer by advancing the fake clock until the condition holds.</summary>
    private static async Task TickUntilAsync(FakeClock clock, Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(_timeout);
        while (!await condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            clock.Advance(_lease / 3);
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task A_batched_job_is_not_leased_until_the_jobs_before_it_finish()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var secondId = 0L;
        JobState? secondWhileFirstRuns = null;
        var first = new Handler("first", async (_, _) => secondWhileFirstRuns = await Store.GetAsync(secondId, Ct));
        var second = new Handler("second", (_, _) => Task.CompletedTask);
        var poller = new JobPoller(Store, [first, second], new FakeClock(), new JobPollerOptions { LeaseDuration = _lease, BatchSize = 2 });
        await Store.EnqueueAsync("first", "{}", ct: Ct);
        secondId = (await Store.EnqueueAsync("second", "{}", ct: Ct))!.Value;

        var claimed = await poller.PollOnceAsync(Ct);

        // While the first job ran, the second was still unclaimed (so its lease had not started).
        Assert.Equal("queued", secondWhileFirstRuns!.State);
        Assert.Null(secondWhileFirstRuns.LeaseOwner);
        Assert.Equal(2, claimed);
        Assert.Equal(1, Assert.Single(second.Seen).Attempts);
        Assert.Equal("succeeded", (await Store.GetAsync(secondId, Ct))!.State);
    }

    [Fact]
    public async Task A_handler_that_outlives_the_lease_keeps_it_and_is_not_run_twice()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var clock = new FakeClock();
        IReadOnlyList<JobRecord>? stolen = null;
        var id = 0L;
        var handler = new Handler("long", async (_, _) =>
        {
            // The lease runs out on the database clock (moved into the past rather than waited for)...
            await SqlAsync("UPDATE job SET lease_until = now() - interval '1 second' WHERE id = @id", id);
            // ...and only the poller's renewal can bring it back.
            await TickUntilAsync(clock, () => LeaseIsLiveAsync(id));
            stolen = await Store.ClaimDueAsync("other-host", _lease, 10, Ct);
        });
        var poller = new JobPoller(Store, [handler], clock, new JobPollerOptions { LeaseDuration = _lease });
        id = (await Store.EnqueueAsync("long", "{}", ct: Ct))!.Value;

        await poller.PollOnceAsync(Ct);

        Assert.Empty(stolen!);
        Assert.Single(handler.Seen);
        var state = await Store.GetAsync(id, Ct);
        Assert.Equal("succeeded", state!.State);
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public async Task A_handler_is_cancelled_when_its_lease_is_taken_over()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var clock = new FakeClock();
        var cancelled = false;
        var handler = new Handler("lost", async (job, ct) =>
        {
            await SqlAsync("UPDATE job SET lease_owner = 'thief' WHERE id = @id", job.Id);
            // The next renewal finds the lease gone and cancels the handler's token.
            await TickUntilAsync(clock, () => Task.FromResult(ct.IsCancellationRequested));
            cancelled = ct.IsCancellationRequested;
        });
        var poller = new JobPoller(Store, [handler], clock, new JobPollerOptions { LeaseDuration = _lease });
        var id = (await Store.EnqueueAsync("lost", "{}", ct: Ct))!.Value;

        await poller.PollOnceAsync(Ct);

        Assert.True(cancelled);
        Assert.Equal("running", (await Store.GetAsync(id, Ct))!.State);
    }
}
