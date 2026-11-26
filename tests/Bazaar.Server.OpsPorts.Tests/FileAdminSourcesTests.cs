using Bazaar.Contracts.Kodo;
using Bazaar.Server.Kodo;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.OpsPorts.Tests;

public sealed class FileAdminSourcesTests : IDisposable
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-admin-src-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AdminStatusView Status() => new("Bazaar server", "0.1.0+abc", "Beta", _t0, 0, ["search: postgres_fts (usable)"]);

    [Fact]
    public async Task Status_reads_the_file_and_counts_uptime_to_now()
    {
        var path = Path.Combine(_dir, "s.json");
        AdminStatusFile.Write(path, Status(), _t0);
        var source = new FileAdminStatusSource(path, new Clock(_t0.AddSeconds(30)));

        var view = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.1.0+abc", view.Version);
        Assert.Equal("Beta", view.Codename);
        Assert.Equal(30, view.UptimeSeconds);
        Assert.Equal(["search: postgres_fts (usable)"], view.ProviderLines);
    }

    [Fact]
    public async Task A_stale_status_file_says_the_server_is_not_reporting_with_zero_uptime()
    {
        var path = Path.Combine(_dir, "s.json");
        AdminStatusFile.Write(path, Status(), _t0);
        var source = new FileAdminStatusSource(path, new Clock(_t0.AddMinutes(10)));

        var view = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, view.UptimeSeconds);
        Assert.StartsWith("Server is not reporting", view.ProviderLines[0], StringComparison.Ordinal);
        Assert.Equal("search: postgres_fts (usable)", view.ProviderLines[1]);
    }

    [Fact]
    public async Task A_missing_or_corrupt_status_file_throws_so_the_screen_reads_unavailable()
    {
        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(bad, "{", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FileAdminStatusSource(Path.Combine(_dir, "none.json"), TimeProvider.System).GetAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FileAdminStatusSource(bad, TimeProvider.System).GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kodo_reads_the_snapshot_file_and_maps_it()
    {
        var path = Path.Combine(_dir, "k.json");
        var snapshot = new KodoStatusSnapshotDto(1, 3, new KodoStatusDto("degraded", null, 72, _t0, []), "ab",
            [new KodoComponentDto("host.disk.free.data", "amber", "disk_usage_high", _t0)]);
        KodoSnapshotFile.Write(path, snapshot);

        var view = await new FileAdminKodoSource(path, new Clock(_t0.AddSeconds(5))).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(72, view.Score);
        Assert.Equal(("host.disk.free.data", "degraded", "disk_usage_high"), (view.Checks[1].Name, view.Checks[1].State, view.Checks[1].Detail));
    }

    [Fact]
    public async Task Kodo_with_no_file_reads_as_not_reported_and_an_old_file_as_unobserved()
    {
        var missing = await new FileAdminKodoSource(Path.Combine(_dir, "none.json"), TimeProvider.System).GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(missing.Score);
        Assert.Equal("unknown", missing.Checks[0].State);

        var path = Path.Combine(_dir, "k.json");
        KodoSnapshotFile.Write(path, new KodoStatusSnapshotDto(1, 3, new KodoStatusDto("healthy", null, 100, _t0, []), null, []));
        var old = await new FileAdminKodoSource(path, new Clock(_t0.AddHours(1))).GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(old.Score);
        Assert.Equal("unobserved", old.Checks[0].State);
    }

    [Fact]
    public void Options_resolve_both_files_the_way_the_writers_do()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Ops:DataDirectory"] = _dir,
            ["Bazaar:Kodo:DataDirectory"] = Path.Combine(_dir, "kodo"),
        }).Build();

        var options = OpsPortsOptions.FromConfiguration(config);

        Assert.Equal(AdminStatusFile.ResolvePath(null, _dir), options.StatusFile);
        Assert.Equal(Path.Combine(_dir, "kodo", KodoSnapshotFile.FileName), options.KodoSnapshotFile);
    }
}
