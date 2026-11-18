using Bazaar.Server.Backup.Offsite;
using Bazaar.Server.Backup.Processes;
using Bazaar.Server.Backup.Restore;

namespace Bazaar.Server.Backup.Tests;

public sealed class OffsiteAndRestoreTests
{
    [Fact]
    public async Task Restic_put_returns_snapshot_id_and_keeps_password_out_of_arguments()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0,
            "{\"message_type\":\"status\"}\n{\"message_type\":\"summary\",\"snapshot_id\":\"abc123\"}\n", string.Empty));
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var env = new Dictionary<string, string> { ["RESTIC_PASSWORD_FILE"] = "/secrets/restic.pw" };
        var target = new ResticBackupTarget(runner, "restic", "s3:example/bucket", env);

        var snap = await target.PutDirectoryAsync(dir.Path, ["bazaar", "base"], TestContext.Current.CancellationToken);

        Assert.Equal("abc123", snap.SnapshotId);
        var call = Assert.Single(runner.Calls);
        Assert.DoesNotContain(call.Args, a => a.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/secrets/restic.pw", call.Env!["RESTIC_PASSWORD_FILE"]);
        Assert.Contains("--tag=base", call.Args);
    }

    [Fact]
    public async Task Restic_failure_is_an_error_naming_the_tool_only()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(3, string.Empty, "repository locked"));
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var target = new ResticBackupTarget(runner, "restic", "r", new Dictionary<string, string>());

        var ex = await Assert.ThrowsAsync<ProcessFailedException>(() => target.PutDirectoryAsync(dir.Path, [], TestContext.Current.CancellationToken));
        Assert.Contains("restic backup", ex.Message);
    }

    [Fact]
    public async Task Restic_list_orders_newest_first()
    {
        var json = "[{\"id\":\"a\",\"time\":\"2026-10-01T00:00:00Z\",\"tags\":[]},{\"id\":\"b\",\"time\":\"2026-10-05T00:00:00Z\",\"tags\":[\"x\"]}]";
        var target = new ResticBackupTarget(new FakeProcessRunner((_, _) => new ProcessResult(0, json, string.Empty)), "restic", "r", new Dictionary<string, string>());

        var list = await target.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["b", "a"], list.Select(s => s.SnapshotId));
    }

    [Fact]
    public async Task Restore_refuses_a_non_empty_target()
    {
        using var dir = new TempDir();
        var set = Path.Combine(dir.Path, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "PG_VERSION"), "16\n");
        var target = Path.Combine(dir.Path, "data");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep-me"), "x");

        await Assert.ThrowsAsync<RestoreTargetNotEmptyException>(() => RestoreRunner.RunAsync(
            new RestoreRequest(set, target, 16, null, null, BaseOnly: true), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(target, "keep-me")));
    }

    [Fact]
    public async Task Restore_refuses_a_schema_mismatch()
    {
        using var dir = new TempDir();
        var set = Path.Combine(dir.Path, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "PG_VERSION"), "16\n");

        await Assert.ThrowsAsync<RestoreSchemaMismatchException>(() => RestoreRunner.RunAsync(
            new RestoreRequest(set, Path.Combine(dir.Path, "data"), 18, null, null, BaseOnly: true), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Point_in_time_restore_writes_quoted_recovery_settings()
    {
        using var dir = new TempDir();
        var set = Path.Combine(dir.Path, "set");
        Directory.CreateDirectory(Path.Combine(set, "global"));
        File.WriteAllText(Path.Combine(set, "PG_VERSION"), "16\n");
        File.WriteAllText(Path.Combine(set, "global", "pg_control"), "ctl");
        File.WriteAllText(Path.Combine(set, "bazaar-backup-set.json"), "{}");
        var data = Path.Combine(dir.Path, "data");

        var result = await RestoreRunner.RunAsync(new RestoreRequest(
            set, data, 16, new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), "cp /archive/%f '%p'"), TestContext.Current.CancellationToken);

        Assert.True(result.PointInTime);
        Assert.Equal(2, result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(data, "global", "pg_control")));
        Assert.False(File.Exists(Path.Combine(data, "bazaar-backup-set.json")));
        Assert.True(File.Exists(Path.Combine(data, "recovery.signal")));
        var conf = File.ReadAllText(Path.Combine(data, "postgresql.auto.conf"));
        Assert.Contains("restore_command = 'cp /archive/%f ''%p'''", conf);
        Assert.Contains("recovery_target_time = '2026-10-30 12:00:00.000000+00'", conf);
    }

    [Fact]
    public async Task Point_in_time_without_restore_command_is_refused()
    {
        using var dir = new TempDir();
        var set = Path.Combine(dir.Path, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "PG_VERSION"), "16\n");

        await Assert.ThrowsAsync<ArgumentException>(() => RestoreRunner.RunAsync(
            new RestoreRequest(set, Path.Combine(dir.Path, "data"), 16, DateTimeOffset.UtcNow, null), TestContext.Current.CancellationToken));
    }
}
