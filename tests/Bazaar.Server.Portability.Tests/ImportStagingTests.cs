namespace Bazaar.Server.Portability.Tests;

public class ImportStagingTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore(params (string, DateTimeOffset)[] jobs) : IImportStagingStore
    {
        public List<string> Deleted { get; } = [];

        public string? FailFor { get; set; }

        public Task<IReadOnlyList<(string JobId, DateTimeOffset VerifiedAt)>> ListVerifiedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>(jobs);

        public Task DeleteAsync(string jobId, CancellationToken ct)
        {
            if (jobId == FailFor)
            {
                throw new IOException("disk");
            }
            Deleted.Add(jobId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Default_retention_is_zero_so_staging_is_due_at_verification()
    {
        Assert.Equal(0, StagingRetention.Default.Hours);
        Assert.True(StagingRetention.Default.IsDue(_t0, _t0));
    }

    [Fact]
    public void Retention_above_72_hours_needs_owner_approval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StagingRetention(73));
        Assert.Equal(96, new StagingRetention(96, ownerApprovedLonger: true).Hours);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StagingRetention(-1));
    }

    [Fact]
    public async Task Sweep_deletes_only_due_jobs_and_retries_failures_later()
    {
        var store = new FakeStore(("old", _t0), ("new", _t0.AddHours(60)), ("bad", _t0)) { FailFor = "bad" };
        var clock = new FixedClock(_t0.AddHours(80));

        var deleted = await ImportStagingSweeper.SweepAsync(store, new StagingRetention(72), clock, CancellationToken.None);

        Assert.Equal(["old"], deleted);
        Assert.Equal(["old"], store.Deleted);
    }

    [Fact]
    public void Batcher_closes_at_the_item_cap_or_the_byte_cap()
    {
        var byItems = ImportBatcher.Split(Enumerable.Range(0, 5), _ => 1, 2, 100);
        Assert.Equal([2, 2, 1], byItems.Select(b => b.Count));

        var byBytes = ImportBatcher.Split(new long[] { 40, 40, 40, 200 }, s => s, 10, 100);
        Assert.Equal([2, 1, 1], byBytes.Select(b => b.Count));
    }

    [Fact]
    public void Stall_detector_flags_a_job_without_progress()
    {
        var limit = TimeSpan.FromMinutes(10);
        Assert.False(ImportStallDetector.IsStalled(_t0, _t0.AddMinutes(9), limit));
        Assert.True(ImportStallDetector.IsStalled(_t0, _t0.AddMinutes(10), limit));
    }

    [Fact]
    public void Rate_limiter_spaces_writes()
    {
        var limiter = new ImportRateLimiter(100);
        Assert.Equal(TimeSpan.Zero, limiter.NextDelay(100, _t0));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.NextDelay(50, _t0));
        Assert.Equal(TimeSpan.FromSeconds(1.5), limiter.NextDelay(1, _t0));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
