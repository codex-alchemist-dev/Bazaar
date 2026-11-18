using Bazaar.Contracts.Backup;
using Bazaar.Server.Backup.Engines;
using Bazaar.Server.Backup.Jobs;
using Bazaar.Server.Backup.Offsite;
using Bazaar.Server.Backup.Retention;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Backup.Tests;

public sealed class RetentionAndJobTests
{
    private static BackupSet Set(string id, int day, string wal) =>
        new(id, "/x/" + id, new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), 10, wal);

    [Fact]
    public void Keeps_newest_sets_and_sets_wal_cutoff_to_oldest_kept()
    {
        var sets = new[] { Set("a", 1, "W1"), Set("c", 3, "W3"), Set("b", 2, "W2") };

        var plan = RetentionPlanner.Plan(sets, 2);

        Assert.Equal(["c", "b"], plan.Keep.Select(s => s.SetId));
        Assert.Equal(["a"], plan.Delete.Select(s => s.SetId));
        Assert.Equal("W2", plan.WalKeepFrom);
    }

    [Fact]
    public void Refuses_to_keep_zero_sets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPlanner.Plan([Set("a", 1, "W1")], 0));
    }

    [Fact]
    public void Offsite_policy_refuses_retention_beyond_erasure_window()
    {
        OffsiteRetentionPolicy.Validate(OffsiteRetentionPolicy.ShippedKeepDaily, OffsiteRetentionPolicy.ShippedKeepWeekly);
        Assert.Throws<ArgumentOutOfRangeException>(() => OffsiteRetentionPolicy.Validate(14, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => OffsiteRetentionPolicy.Validate(36, 1));
    }

    [Fact]
    public async Task Job_keeps_only_the_newest_sets_after_repeated_success()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));
        var log = new InMemoryBackupRunLog();
        var job = new BaseBackupJob(new FakeEngine(time), dir.Path, baseSetsKept: 2, time, log);

        for (var i = 0; i < 3; i++)
        {
            var run = await job.RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(BackupState.Succeeded, run.State);
            time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(2, BaseBackupJob.DiscoverSets(dir.Path).Count());
        Assert.Equal(3, log.Recent(10).Count(r => r.State == BackupState.Succeeded));
    }

    [Fact]
    public async Task Failed_engine_run_is_recorded_and_deletes_nothing()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));
        var log = new InMemoryBackupRunLog();
        await new BaseBackupJob(new FakeEngine(time), dir.Path, 1, time, log).RunAsync(TestContext.Current.CancellationToken);
        var before = BaseBackupJob.DiscoverSets(dir.Path).Select(s => s.SetId).Order().ToList();

        var run = await new BaseBackupJob(new FakeEngine(time, fail: true), dir.Path, 1, time, log).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackupState.Failed, run.State);
        Assert.Contains("InvalidOperationException", run.Detail);
        Assert.Equal(before, BaseBackupJob.DiscoverSets(dir.Path).Select(s => s.SetId).Order().ToList());
    }

    /// <summary>Writes a directory with a backup_label and PG_VERSION; each call gets the next WAL segment name.</summary>
    private sealed class FakeEngine(TimeProvider time, bool fail = false) : IBackupEngine
    {
        private int _seq;

        public string Name => "fake";

        public Task<BackupSet> TakeBaseBackupAsync(string destinationDirectory, CancellationToken ct)
        {
            if (fail)
                throw new InvalidOperationException("scripted failure");
            Directory.CreateDirectory(destinationDirectory);
            var wal = (++_seq).ToString("X24", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(destinationDirectory, "backup_label"), $"START WAL LOCATION: 0/1 (file {wal})\n");
            File.WriteAllText(Path.Combine(destinationDirectory, "PG_VERSION"), "16\n");
            File.WriteAllBytes(Path.Combine(destinationDirectory, "base.bin"), new byte[64]);
            return Task.FromResult(new BackupSet("fake-" + wal, destinationDirectory, time.GetUtcNow(), 64, wal));
        }
    }
}
