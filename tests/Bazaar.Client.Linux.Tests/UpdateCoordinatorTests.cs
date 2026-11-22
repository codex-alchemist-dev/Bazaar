using Bazaar.Client.Linux.Updates;
using Bazaar.Client.Updater.Linux;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Linux.Tests;

public class UpdateCoordinatorTests
{
    private static readonly DateTimeOffset _at = DateTimeOffset.UnixEpoch;

    private static WelcomeCompatibilityHolder KnownServer()
    {
        var holder = new WelcomeCompatibilityHolder("1.0");
        holder.Record(new Welcome(1, 0, "json", Guid.Empty, _at, 20, 60, new WireLimits(1024, 8, 50), [], 0, null, null, "7", false));
        return holder;
    }

    private static (UpdateCoordinator Coordinator, FakeUpdater Updater, List<string> Log) Build(
        FakeUpdater updater, WelcomeCompatibilityHolder? compat = null, TimeProvider? clock = null, TimeSpan? delay = null)
    {
        var log = new List<string>();
        var coordinator = new UpdateCoordinator(updater, null, compat ?? KnownServer(), clock ?? TimeProvider.System, delay ?? TimeSpan.Zero, a => a(), log.Add);
        return (coordinator, updater, log);
    }

    [Fact]
    public async Task Available_release_is_staged_and_reported_as_ready_after_restart()
    {
        var (coordinator, updater, _) = Build(new FakeUpdater(Result(UpdateStatus.Available, "0.2.0"), Result(UpdateStatus.Installed, "0.2.0")));

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.ReadyAfterRestart, notice.Kind);
        Assert.Contains("0.2.0", notice.Text);
        Assert.Contains("restart", notice.Text);
        Assert.Equal(1, updater.Applies);
        Assert.Same(notice, coordinator.Notice);
    }

    [Fact]
    public async Task Up_to_date_is_reported_and_nothing_is_applied()
    {
        var (coordinator, updater, _) = Build(new FakeUpdater(Result(UpdateStatus.UpToDate, "0.1.0")));

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.UpToDate, notice.Kind);
        Assert.Equal(0, updater.Applies);
    }

    [Fact]
    public async Task Unknown_server_facts_refuse_the_apply()
    {
        var (coordinator, updater, _) = Build(
            new FakeUpdater(Result(UpdateStatus.Available, "0.2.0"), Result(UpdateStatus.Installed, "0.2.0")),
            new WelcomeCompatibilityHolder("1.0"));

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.Failed, notice.Kind);
        Assert.Contains("not reported", notice.Text);
        Assert.Equal(0, updater.Applies);
    }

    [Fact]
    public async Task Welcome_without_a_schema_version_refuses_the_apply()
    {
        var holder = new WelcomeCompatibilityHolder("1.0");
        holder.Record(new Welcome(1, 0, "json", Guid.Empty, _at, 20, 60, new WireLimits(1024, 8, 50), [], 0, null, null));
        var (coordinator, updater, _) = Build(new FakeUpdater(Result(UpdateStatus.Available, "0.2.0")), holder);

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.Failed, notice.Kind);
        Assert.Equal(0, updater.Applies);
    }

    [Fact]
    public async Task Upgrade_required_welcome_refuses_the_apply()
    {
        var holder = new WelcomeCompatibilityHolder("1.0");
        holder.Record(new Welcome(2, 0, "json", Guid.Empty, _at, 20, 60, new WireLimits(1024, 8, 50), [], 0, true, "0.9.0", "7", false));
        var (coordinator, updater, _) = Build(new FakeUpdater(Result(UpdateStatus.Available, "0.2.0")), holder);

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.Failed, notice.Kind);
        Assert.Equal(0, updater.Applies);
    }

    [Fact]
    public async Task Updater_refusal_is_shown_with_its_reason()
    {
        var (coordinator, _, _) = Build(new FakeUpdater(Result(UpdateStatus.SignatureInvalid, "0.2.0", "bad signature")));

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.Failed, notice.Kind);
        Assert.Contains("SignatureInvalid", notice.Text);
        Assert.Contains("bad signature", notice.Text);
    }

    [Fact]
    public async Task Exception_from_the_updater_becomes_a_notice_not_a_throw()
    {
        var updater = new FakeUpdater { CheckThrows = new HttpRequestException("network down") };
        var (coordinator, _, log) = Build(updater);

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.Failed, notice.Kind);
        Assert.Contains("network down", notice.Text);
        Assert.Contains(log, line => line.Contains("network down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_coordinator_reports_why_and_builds_nothing()
    {
        using var coordinator = new UpdateCoordinator(null, "No signing key is pinned.", new WelcomeCompatibilityHolder("1.0"), TimeProvider.System, TimeSpan.Zero, a => a(), _ => { });

        var notice = await coordinator.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateNoticeKind.Disabled, notice.Kind);
        Assert.Equal("No signing key is pinned.", notice.Text);
    }

    [Fact]
    public async Task Background_check_waits_for_the_start_delay_then_runs_once()
    {
        var clock = new ManualTimeProvider();
        var (coordinator, updater, _) = Build(
            new FakeUpdater(Result(UpdateStatus.Available, "0.2.0"), Result(UpdateStatus.Installed, "0.2.0")),
            clock: clock,
            delay: TimeSpan.FromSeconds(30));

        var task = coordinator.StartBackgroundCheck();
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, updater.Checks);
        Assert.Null(coordinator.Notice);

        clock.FireTimers();
        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, updater.Checks);
        Assert.Equal(UpdateNoticeKind.ReadyAfterRestart, coordinator.Notice!.Kind);
    }

    [Fact]
    public async Task Background_check_is_quiet_when_up_to_date()
    {
        var (coordinator, _, _) = Build(new FakeUpdater(Result(UpdateStatus.UpToDate, "0.1.0")));

        await coordinator.StartBackgroundCheck().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(coordinator.Notice);
    }

    [Fact]
    public async Task Disposing_cancels_a_pending_background_check()
    {
        var clock = new ManualTimeProvider();
        var (coordinator, updater, _) = Build(new FakeUpdater(Result(UpdateStatus.UpToDate, "0.1.0")), clock: clock, delay: TimeSpan.FromSeconds(30));
        var task = coordinator.StartBackgroundCheck();
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        coordinator.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, updater.Checks);
    }

    private static UpdateResult Result(UpdateStatus status, string? version, string detail = "") => new(status, version, detail, _at);

    private sealed class FakeUpdater(params UpdateResult[] results) : ILinuxUpdater
    {
        private readonly Queue<UpdateResult> _results = new(results);

        public int Checks { get; private set; }

        public int Applies { get; private set; }

        public Exception? CheckThrows { get; init; }

        public Task<UpdateResult> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return CheckThrows is null ? Task.FromResult(_results.Dequeue()) : Task.FromException<UpdateResult>(CheckThrows);
        }

        public Task<UpdateResult> ApplyAsync(CancellationToken ct)
        {
            Applies++;
            return Task.FromResult(_results.Dequeue());
        }

        public UpdateResult Rollback() => throw new NotSupportedException();
    }

    /// <summary>A clock whose timers fire only when the test says so.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _created.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state));
            lock (_timers)
            {
                _timers.Add(timer);
            }

            _created.TrySetResult();
            return timer;
        }

        public void FireTimers()
        {
            ManualTimer[] due;
            lock (_timers)
            {
                due = [.. _timers];
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }
    }

    private sealed class ManualTimer(Action fire) : ITimer
    {
        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
