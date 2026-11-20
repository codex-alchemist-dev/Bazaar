using Avalonia.Headless.XUnit;
using Bazaar.Client.Commands;
using Bazaar.Client.Linux.Updates;
using Bazaar.Client.Updater.Linux;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Linux.Tests;

public class UpdatesTests
{
    private static readonly LinuxPaths _paths = new("/s/bazaar", "/d/bazaar");
    private static readonly UpdateBuildConstants _build = new("abcd", "/k/key.asc", IsOwnerInstall: false);

    private static LinuxUpdaterSettings Read(Dictionary<string, string> env) =>
        LinuxUpdaterSettingsReader.FromEnvironment(k => env.GetValueOrDefault(k), _paths, "0.1.0", "linux-x64", _build);

    [Fact]
    public void Settings_default_to_stable_under_the_data_directory()
    {
        var s = Read([]);

        Assert.Equal(ReleaseChannels.Stable, s.Channel);
        Assert.Equal(Path.Combine("/d/bazaar", "client"), s.InstallRoot);
        Assert.Equal("abcd", s.PinnedKeyFingerprint);
        Assert.False(s.IsOwnerInstall);
        Assert.Empty(s.SkipVersions);
    }

    [Fact]
    public void Settings_read_operator_values()
    {
        var s = Read(new()
        {
            [LinuxUpdaterSettingsReader.ChannelVariable] = "Beta",
            [LinuxUpdaterSettingsReader.SkipVersionsVariable] = "0.2.0, 0.3.0",
            [LinuxUpdaterSettingsReader.GpgTimeoutVariable] = "30",
            [LinuxUpdaterSettingsReader.MaxArchiveEntriesVariable] = "500",
        });

        Assert.Equal("Beta", s.Channel);
        Assert.Equal(["0.2.0", "0.3.0"], s.SkipVersions);
        Assert.Equal(TimeSpan.FromSeconds(30), s.GpgTimeout);
        Assert.Equal(500, s.MaxArchiveEntries);
    }

    [Theory]
    [InlineData(LinuxUpdaterSettingsReader.ChannelVariable, "stable")]
    [InlineData(LinuxUpdaterSettingsReader.GpgTimeoutVariable, "0")]
    [InlineData(LinuxUpdaterSettingsReader.MaxPackageBytesVariable, "99999999999999")]
    [InlineData(LinuxUpdaterSettingsReader.MaxExtractedBytesVariable, "-1")]
    [InlineData(LinuxUpdaterSettingsReader.MaxArchiveEntriesVariable, "100001")]
    public void Settings_reject_unknown_or_loosening_values(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => Read(new() { [name] = value }));
    }

    [Fact]
    public void Owner_flag_comes_from_the_build_not_the_environment()
    {
        var s = LinuxUpdaterSettingsReader.FromEnvironment(
            k => k == "BAZAAR_UPDATE_OWNER" ? "true" : null, _paths, "0.1.0", "linux-x64", _build with { IsOwnerInstall = true });

        Assert.True(s.IsOwnerInstall);
        Assert.False(Read(new() { ["BAZAAR_UPDATE_OWNER"] = "true" }).IsOwnerInstall);
    }

    [Fact]
    public void Paths_follow_xdg_data_home()
    {
        var p = LinuxPaths.FromEnvironment(k => k == "XDG_DATA_HOME" ? "/srv/data" : null, "/home/dom");
        Assert.Equal(Path.Combine("/srv/data", "bazaar", "client"), p.InstallRoot);

        var q = LinuxPaths.FromEnvironment(_ => null, "/home/dom");
        Assert.Equal(Path.Combine("/home/dom", ".local", "share", "bazaar"), q.DataDirectory);
    }

    [Fact]
    public void Check_command_is_a_valid_local_command()
    {
        var load = CommandRegistry.Load([UpdateCoordinator.CheckCommand], []);

        Assert.Single(load.Registry.Commands);
        Assert.Equal("Check for updates", UpdateCoordinator.CheckCommand.Title);
    }

    [Fact]
    public async Task Check_now_stages_an_available_release_and_says_it_applies_on_restart()
    {
        var updater = new FakeUpdater(new(UpdateStatus.Available, "0.2.0", "", DateTimeOffset.UtcNow))
        { ApplyResult = new(UpdateStatus.Installed, "0.2.0", "", DateTimeOffset.UtcNow) };
        var c = new UpdateCoordinator(updater, a => a(), _ => { });

        var result = await c.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.Installed, result.Status);
        Assert.Equal(1, updater.Applies);
        Assert.Contains("next time you restart", c.Notice);
    }

    [Fact]
    public async Task Check_now_fails_closed_with_a_notice_and_never_applies()
    {
        var updater = new FakeUpdater(new(UpdateStatus.CompatibilityUnknown, null, "server status unknown", DateTimeOffset.UtcNow));
        var c = new UpdateCoordinator(updater, a => a(), _ => { });

        await c.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, updater.Applies);
        Assert.Contains("CompatibilityUnknown", c.Notice);
    }

    [Fact]
    public async Task Background_check_only_checks_and_logs_failures_without_throwing()
    {
        var log = new List<string>();
        var ok = new FakeUpdater(new(UpdateStatus.Available, "0.2.0", "", DateTimeOffset.UtcNow));
        var c = new UpdateCoordinator(ok, a => a(), log.Add);
        await c.StartBackgroundCheck(TestContext.Current.CancellationToken);
        Assert.Equal(0, ok.Applies);
        Assert.Contains("0.2.0", c.Notice);

        var bad = new FakeUpdater(null) { Throw = new IOException("offline") };
        var c2 = new UpdateCoordinator(bad, a => a(), log.Add);
        await c2.StartBackgroundCheck(TestContext.Current.CancellationToken);
        Assert.Null(c2.Notice);
        Assert.Contains(log, l => l.Contains("offline"));
    }

    [Fact]
    public async Task Up_to_date_background_check_stays_quiet()
    {
        var c = new UpdateCoordinator(new FakeUpdater(new(UpdateStatus.UpToDate, "0.1.0", "", DateTimeOffset.UtcNow)), a => a(), _ => { });
        await c.StartBackgroundCheck(TestContext.Current.CancellationToken);
        Assert.Null(c.Notice);
    }

    [AvaloniaFact]
    public async Task Notice_view_shows_and_hides_with_the_coordinator_notice()
    {
        var updater = new FakeUpdater(new(UpdateStatus.UpToDate, "0.1.0", "", DateTimeOffset.UtcNow));
        var c = new UpdateCoordinator(updater, a => a(), _ => { });
        var view = new UpdateNoticeView(c);
        Assert.False(view.IsVisible);

        await c.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.True(view.IsVisible);
        Assert.Equal("Bazaar is up to date.", view.Text);
    }

    private sealed class FakeUpdater(UpdateResult? check) : ILinuxUpdater
    {
        public UpdateResult? ApplyResult { get; init; }
        public Exception? Throw { get; init; }
        public int Applies { get; private set; }

        public Task<UpdateResult> CheckAsync(CancellationToken ct) =>
            Throw is not null ? Task.FromException<UpdateResult>(Throw) : Task.FromResult(check!);

        public Task<UpdateResult> ApplyAsync(CancellationToken ct)
        {
            Applies++;
            return Task.FromResult(ApplyResult!);
        }

        public UpdateResult Rollback() => throw new NotSupportedException();
    }
}
