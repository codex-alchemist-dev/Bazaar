using Bazaar.Client.Linux.Updates;
using Bazaar.Client.Updater.Linux;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Linux.Tests;

public class UpdateRestartTests
{
    private static readonly DateTimeOffset _at = DateTimeOffset.UnixEpoch;

    private static (UpdateRestart Restart, List<string> Events, List<RestartRequest> Scheduled) Build(bool staged, string? path, params string[] args)
    {
        var events = new List<string>();
        var scheduled = new List<RestartRequest>();
        var restart = new UpdateRestart(
            () => staged,
            () => path,
            args,
            () => events.Add("shutdown"),
            r =>
            {
                events.Add("schedule");
                scheduled.Add(r);
            });
        return (restart, events, scheduled);
    }

    [Fact]
    public void Refuses_when_nothing_is_staged()
    {
        var (restart, events, scheduled) = Build(false, "/opt/bazaar/Bazaar.Client.Linux");

        var refusal = restart.TryRestart();

        Assert.Contains("No update is staged", refusal);
        Assert.Empty(events);
        Assert.Empty(scheduled);
        Assert.False(restart.CanRestart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bazaar")]
    [InlineData("./Bazaar.Client.Linux")]
    [InlineData("/usr/bin/dotnet")]
    public void Refuses_a_bad_process_path(string? path)
    {
        var (restart, events, _) = Build(true, path);

        Assert.NotNull(restart.TryRestart());
        Assert.Empty(events);
    }

    [Fact]
    public void Builds_the_request_without_one_shot_flags_and_shuts_down_after_scheduling()
    {
        var (restart, events, scheduled) = Build(true, "/opt/bazaar/Bazaar.Client.Linux", "--theme", "paper", "--sign-in", "--first-run", "--preload", "--terminal");

        Assert.Null(restart.TryRestart());

        var request = Assert.Single(scheduled);
        Assert.Equal("/opt/bazaar/Bazaar.Client.Linux", request.FileName);
        Assert.Equal(["--theme", "paper", "--terminal"], request.Arguments);
        Assert.Equal(["schedule", "shutdown"], events);
    }

    [Fact]
    public void A_second_press_does_not_schedule_twice()
    {
        var (restart, events, scheduled) = Build(true, "/opt/bazaar/Bazaar.Client.Linux");

        Assert.Null(restart.TryRestart());
        Assert.Null(restart.TryRestart());

        Assert.Single(scheduled);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Pending_restart_starts_once_and_logs_a_failure()
    {
        PendingRestart.Set(new RestartRequest("/opt/bazaar/Bazaar.Client.Linux", ["--x"]));
        var started = new List<RestartRequest>();

        PendingRestart.StartIfScheduled(started.Add, _ => { });
        PendingRestart.StartIfScheduled(started.Add, _ => { });

        Assert.Single(started);

        PendingRestart.Set(new RestartRequest("/nonexistent/bazaar", []));
        var log = new List<string>();
        PendingRestart.StartIfScheduled(_ => throw new InvalidOperationException("boom"), log.Add);
        Assert.Contains("boom", Assert.Single(log));
    }

    [Fact]
    public async Task Restart_command_follows_the_staged_state()
    {
        var holder = new WelcomeCompatibilityHolder("1.0");
        holder.Record(new Welcome(1, 0, "json", Guid.Empty, _at, 20, 60, new WireLimits(1024, 8, 50), [], 0, null, null, "7", false));
        var updater = new StagingUpdater();
        using var coordinator = new UpdateCoordinator(updater, null, holder, TimeProvider.System, TimeSpan.Zero, a => a(), _ => { });
        var command = UpdateHost.CreateRestartCommand(coordinator);

        Assert.Equal("update.restart", command.Id);
        Assert.False(command.IsEnabled());

        await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.True(coordinator.IsStaged);
        Assert.Equal("0.2.0", coordinator.StagedVersion);
        Assert.True(command.IsEnabled());
    }

    private sealed class StagingUpdater : ILinuxUpdater
    {
        public Task<UpdateResult> CheckAsync(CancellationToken ct) => Task.FromResult(new UpdateResult(UpdateStatus.Available, "0.2.0", "", _at));

        public Task<UpdateResult> ApplyAsync(CancellationToken ct) => Task.FromResult(new UpdateResult(UpdateStatus.Installed, "0.2.0", "", _at));

        public UpdateResult Rollback() => throw new NotSupportedException();
    }
}
