using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PostgresProcessControllerTests : IDisposable
{
    private readonly string _dataDirectory = ProcessTestSupport.NewTempDirectory();
    private readonly List<TcpListener> _listeners = [];
    private static readonly int[] _expectedRestartSeconds = [1, 2, 4, 8, 16, 30, 30, 30];

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task InitDb_sets_the_password_over_stdin_and_leaves_no_trust_rule()
    {
        var runner = new FakePostgresCommandRunner();
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, 54999), runner);
        string? persisted = null;

        await controller.InitDbAsync((password, _) => { persisted = password; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        var initdb = runner.Runs.Single(run => Path.GetFileNameWithoutExtension(run.FileName) == "initdb");
        Assert.Equal("trust", Argument(initdb.Arguments, "-A"));
        Assert.Equal("bazaar_admin", Argument(initdb.Arguments, "-U"));
        Assert.Equal("UTF8", Argument(initdb.Arguments, "-E"));
        Assert.DoesNotContain("--pwfile", initdb.Arguments);

        // The password reaches the tools only on stdin, never in an argument list.
        Assert.All(runner.Runs, run => Assert.DoesNotContain(persisted, string.Join(' ', run.Arguments), StringComparison.Ordinal));
        var setter = Assert.Single(runner.StandardInputs, input => input is not null && input.Contains("ALTER ROLE", StringComparison.Ordinal));
        Assert.Contains($"ALTER ROLE \"bazaar_admin\" PASSWORD '{persisted}';", setter, StringComparison.Ordinal);

        // The bootstrap server is stopped, and pg_hba.conf no longer allows trust.
        Assert.Contains(runner.Runs, run => Path.GetFileNameWithoutExtension(run.FileName) == "pg_ctl" && run.Arguments.Contains("stop"));
        var hba = File.ReadAllText(Path.Combine(_dataDirectory, "pg_hba.conf"));
        Assert.False(PgHbaRules.HasTrustRule(hba));
        Assert.Contains("scram-sha-256", hba, StringComparison.Ordinal);

        // The pending marker is gone once the bootstrap has finished.
        Assert.False(File.Exists(_dataDirectory + ".bootstrap-pending"));
    }

    [Fact]
    public async Task InitDb_failure_message_never_contains_the_password()
    {
        var runner = new FakePostgresCommandRunner();
        string? persisted = null;
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, 54999), runner);
        // Only the password statement (psql -f -) fails. The readiness probe (psql -c) succeeds.
        runner.OnRun = (fileName, arguments) =>
            Path.GetFileNameWithoutExtension(fileName) == "psql" && arguments.Contains("-f")
                ? new PostgresCommandResult(1, "leaked: " + persisted)
                : new PostgresCommandResult(0, string.Empty);

        var ex = await Assert.ThrowsAsync<PostgresProcessException>(() =>
            controller.InitDbAsync((password, _) => { persisted = password; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken));

        Assert.NotNull(persisted);
        Assert.DoesNotContain(persisted, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_bootstrap_marker_blocks_start()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        File.WriteAllText(_dataDirectory + ".bootstrap-pending", "in progress\n");
        try
        {
            var runner = new FakePostgresCommandRunner();
            var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, ProcessTestSupport.FreePort()), runner);

            var ex = await Assert.ThrowsAsync<PostgresProcessException>(() => controller.StartAsync(TestContext.Current.CancellationToken));

            Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Children);
        }
        finally
        {
            File.Delete(_dataDirectory + ".bootstrap-pending");
        }
    }

    [Fact]
    public async Task InitDb_refuses_a_data_directory_that_already_holds_a_cluster()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var runner = new FakePostgresCommandRunner();
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, 54999), runner);

        await Assert.ThrowsAsync<PostgresProcessException>(() =>
            controller.InitDbAsync((_, _) => ValueTask.CompletedTask, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Start_times_out_when_postgres_never_accepts_connections()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        var runner = new FakePostgresCommandRunner();
        var options = ProcessTestSupport.OptionsFor(_dataDirectory, port);
        options.StartupTimeout = TimeSpan.FromMilliseconds(300);
        var controller = new PostgresProcessController(options, runner);

        var ex = await Assert.ThrowsAsync<PostgresProcessException>(() => controller.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("did not accept connections", ex.Message, StringComparison.Ordinal);
        var child = Assert.Single(runner.Children);
        Assert.Equal(1, child.KillCount);
        Assert.Equal(PostgresProcessState.Stopped, controller.State);
    }

    [Fact]
    public async Task Start_refuses_when_the_port_is_held_by_a_listener_that_is_not_this_cluster()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        _listeners.Add(ProcessTestSupport.ListenOn(port));
        var runner = new FakePostgresCommandRunner();
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);

        var ex = await Assert.ThrowsAsync<PostgresProcessException>(() => controller.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("held by another process", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.StartArguments);
    }

    [Fact]
    public async Task Start_adopts_a_cluster_whose_postmaster_pid_names_a_live_process_on_the_same_port()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        ProcessTestSupport.WritePostmasterPid(_dataDirectory, Environment.ProcessId, port);
        _listeners.Add(ProcessTestSupport.ListenOn(port));
        var runner = new FakePostgresCommandRunner();
        runner.AlivePids.Add(Environment.ProcessId);
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);
        var events = new ConcurrentQueue<PostgresProcessEvent>();
        controller.ProcessEvent += events.Enqueue;

        await controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(controller.IsAdopted);
        Assert.Equal(PostgresProcessState.Running, controller.State);
        Assert.Empty(runner.StartArguments);
        Assert.Equal(1, runner.AttachCount);
        Assert.Contains(events, e => e.Kind == PostgresProcessEventKind.Adopted);

        runner.OnRun = (_, arguments) =>
        {
            if (arguments.Contains("fast"))
            {
                foreach (var child in runner.Children)
                {
                    child.Exit(0);
                }
            }

            return new PostgresCommandResult(0, string.Empty);
        };
        await controller.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PostgresProcessState.Stopped, controller.State);
        Assert.False(controller.IsAdopted);
        Assert.Contains(runner.Runs, run => run.Arguments.Contains("fast"));
    }

    [Fact]
    public async Task Start_refuses_a_live_cluster_recorded_on_a_different_port()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        ProcessTestSupport.WritePostmasterPid(_dataDirectory, Environment.ProcessId, port + 1);
        var runner = new FakePostgresCommandRunner();
        runner.AlivePids.Add(Environment.ProcessId);
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);

        var ex = await Assert.ThrowsAsync<PostgresProcessException>(() => controller.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Refusing to start a second server", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.StartArguments);
        Assert.Equal(0, runner.AttachCount);
    }

    [Fact]
    public async Task Start_leaves_a_stale_postmaster_pid_file_in_place()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        ProcessTestSupport.WritePostmasterPid(_dataDirectory, 999_999, port);
        var runner = new FakePostgresCommandRunner();
        runner.OnChildStarted = child => _listeners.Add(ListenAndTieTo(child, port));
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);

        await controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.Single(runner.StartArguments);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "postmaster.pid")));
        await controller.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stop_requests_a_fast_shutdown_and_does_not_restart()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        var runner = new FakePostgresCommandRunner();
        runner.OnChildStarted = child => _listeners.Add(ListenAndTieTo(child, port));
        runner.OnRun = (_, arguments) =>
        {
            if (arguments.Contains("fast"))
            {
                foreach (var child in runner.Children)
                {
                    child.Exit(0);
                }
            }

            return new PostgresCommandResult(0, string.Empty);
        };
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);
        var events = new ConcurrentQueue<PostgresProcessEvent>();
        controller.ProcessEvent += events.Enqueue;
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PostgresProcessState.Stopped, controller.State);
        Assert.Single(runner.Children);
        Assert.DoesNotContain(events, e => e.Kind == PostgresProcessEventKind.RestartScheduled);
        Assert.DoesNotContain(events, e => e.Kind == PostgresProcessEventKind.ForcedShutdown);
    }

    [Fact]
    public async Task Stop_forces_the_child_when_fast_and_immediate_shutdown_do_not_finish()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        var runner = new FakePostgresCommandRunner();
        runner.OnChildStarted = child => _listeners.Add(ListenAndTieTo(child, port));
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);
        var events = new ConcurrentQueue<PostgresProcessEvent>();
        controller.ProcessEvent += events.Enqueue;
        await controller.StartAsync(TestContext.Current.CancellationToken);

        await controller.StopAsync(TestContext.Current.CancellationToken);

        var child = Assert.Single(runner.Children);
        Assert.Equal(1, child.KillCount);
        Assert.Contains(runner.Runs, run => run.Arguments.Contains("fast"));
        Assert.Contains(runner.Runs, run => run.Arguments.Contains("immediate"));
        Assert.Contains(events, e => e.Kind == PostgresProcessEventKind.ForcedShutdown);
    }

    [Fact]
    public async Task Start_passes_port_and_listen_address_to_postgres()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        var runner = new FakePostgresCommandRunner();
        runner.OnChildStarted = child => _listeners.Add(ListenAndTieTo(child, port));
        var controller = new PostgresProcessController(ProcessTestSupport.OptionsFor(_dataDirectory, port), runner);

        await controller.StartAsync(TestContext.Current.CancellationToken);

        var arguments = Assert.Single(runner.StartArguments);
        Assert.Equal(_dataDirectory, Argument(arguments, "-D"));
        Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), Argument(arguments, "-p"));
        Assert.Equal("127.0.0.1", Argument(arguments, "-h"));
        await controller.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Supervisor_restarts_after_unexpected_exits_and_gives_up_at_the_limit()
    {
        ProcessTestSupport.MarkAsCluster(_dataDirectory);
        var port = ProcessTestSupport.FreePort();
        var runner = new FakePostgresCommandRunner();
        runner.OnChildStarted = child => _listeners.Add(ListenAndTieTo(child, port));
        var options = ProcessTestSupport.OptionsFor(_dataDirectory, port);
        options.MaxRestartAttempts = 2;
        var controller = new PostgresProcessController(options, runner);
        var events = new ConcurrentQueue<PostgresProcessEvent>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ProcessEvent += e =>
        {
            events.Enqueue(e);
            if (e.Kind is PostgresProcessEventKind.Started or PostgresProcessEventKind.Restarted)
            {
                runner.Children[^1].Exit(1);
            }

            if (e.Kind == PostgresProcessEventKind.RestartsExhausted)
            {
                exhausted.TrySetResult();
            }
        };

        await controller.StartAsync(TestContext.Current.CancellationToken);
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(3, runner.Children.Count);
        Assert.Equal(PostgresProcessState.Failed, controller.State);
        var delays = events
            .Where(e => e.Kind == PostgresProcessEventKind.RestartScheduled)
            .Select(e => e.RestartDelay!.Value)
            .ToArray();
        var expectedDelays = new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20) };
        Assert.Equal(expectedDelays, delays);
        await controller.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Restart_backoff_doubles_and_never_exceeds_the_maximum()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(30);

        var delays = Enumerable.Range(1, 8)
            .Select(attempt => PostgresRestartBackoff.DelayFor(attempt, baseDelay, maxDelay))
            .ToArray();

        Assert.Equal(
            _expectedRestartSeconds.Select(seconds => TimeSpan.FromSeconds(seconds)).ToArray(),
            delays);
        Assert.Equal(maxDelay, PostgresRestartBackoff.DelayFor(10_000, baseDelay, maxDelay));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresRestartBackoff.DelayFor(0, baseDelay, maxDelay));
    }

    [Fact]
    public void Options_reject_an_invalid_port_and_inverted_backoff()
    {
        var invalidPort = ProcessTestSupport.OptionsFor(_dataDirectory, 70000);
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidPort.Validate());

        var inverted = ProcessTestSupport.OptionsFor(_dataDirectory, 54320);
        inverted.RestartMaxDelay = TimeSpan.FromMilliseconds(1);
        Assert.Throws<ArgumentException>(() => inverted.Validate());
    }

    private static int IndexOf(string[] arguments, string value)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Argument(string[] arguments, string flag) => arguments[IndexOf(arguments, flag) + 1];

    /// <summary>Stands in for postgres: listens on the port while the child runs, and closes the listener when it exits.</summary>
    private static TcpListener ListenAndTieTo(FakePostgresChild child, int port)
    {
        var listener = ProcessTestSupport.ListenOn(port);
        child.OnExited = listener.Stop;
        return listener;
    }
}
