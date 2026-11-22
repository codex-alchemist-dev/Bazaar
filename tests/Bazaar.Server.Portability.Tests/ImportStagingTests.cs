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
    public void Default_and_minimum_retention_is_72_hours()
    {
        Assert.Equal(72, StagingRetention.Default.Hours);
        Assert.False(StagingRetention.Default.IsDue(_t0, _t0.AddHours(71)));
        Assert.True(StagingRetention.Default.IsDue(_t0, _t0.AddHours(72)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StagingRetention(71));
    }

    [Fact]
    public async Task Longer_retention_is_an_audited_admin_change()
    {
        var audit = new InMemoryAuditSink();
        var longer = await StagingRetention.Default.ChangeAsync(168, "admin-1", audit, TimeProvider.System, CancellationToken.None);

        Assert.Equal(168, longer.Hours);
        var entry = Assert.Single(audit.Records);
        Assert.Equal(StagingRetention.ChangeAuditKind, entry.Kind);
        Assert.Equal("admin-1", entry.JobId);
        Assert.Equal("72->168", entry.ContentHash);
    }

    [Fact]
    public async Task A_failed_audit_leaves_the_setting_unchanged()
    {
        var audit = new InMemoryAuditSink { FailAppends = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StagingRetention.Default.ChangeAsync(168, "admin-1", audit, TimeProvider.System, CancellationToken.None));
        Assert.Equal(72, StagingRetention.Default.Hours);
    }

    [Fact]
    public async Task Sweep_deletes_only_due_jobs_and_retries_failures_later()
    {
        var store = new FakeStore(("old", _t0), ("new", _t0.AddHours(60)), ("bad", _t0)) { FailFor = "bad" };
        var clock = new FixedClock(_t0.AddHours(80));

        var deleted = await ImportStagingSweeper.SweepAsync(store, StagingRetention.Default, clock, CancellationToken.None);

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
