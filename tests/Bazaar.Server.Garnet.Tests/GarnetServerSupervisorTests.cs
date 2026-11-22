using Bazaar.Server.Valkey;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>The managed child's lifecycle: configuration, port choice, pid file, start, crash, backoff, breaker, sweep and stop. Time is fake.</summary>
public class GarnetServerSupervisorTests
{
    private static readonly TimeSpan _step = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Unmanaged_cache_is_disabled_and_launches_nothing()
    {
        var harness = new Harness(new GarnetCacheOptions { Managed = false });

        await harness.Supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ValkeyChildState.Disabled, harness.Supervisor.State);
        Assert.Empty(harness.Launcher.Launches);
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Managed_child_gets_its_launch_arguments_and_a_loopback_port()
    {
        var harness = new Harness(Managed(port: 16379));

        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Running);

        var spec = Assert.Single(harness.Launcher.Launches);
        Assert.Equal("Garnet.exe", spec.Executable);
        Assert.Equal(harness.Connection.PasswordForChild(), spec.Arguments[spec.Arguments.ToList().IndexOf("--password") + 1]);
        Assert.Contains("--bind", spec.Arguments);
        Assert.DoesNotContain("-r", spec.Arguments);
        Assert.Equal(16379, harness.Connection.Port);
        Assert.True(harness.Connection.IsReady);
        Assert.Equal($"127.0.0.1:16379,password={harness.Connection.PasswordForChild()}", Assert.Single(harness.Factory.Endpoints));
        Assert.Equal(0, harness.Supervisor.Restarts);
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Port_zero_picks_a_free_loopback_port_at_start()
    {
        var harness = new Harness(Managed(port: 0));

        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Running);

        var spec = Assert.Single(harness.Launcher.Launches);
        var port = harness.Connection.Port;
        Assert.InRange(port, 1, 65535);
        Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), spec.Arguments[spec.Arguments.ToList().IndexOf("--port") + 1]);
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_password_is_never_written_to_the_runtime_directory_and_the_pid_file_names_the_child()
    {
        var harness = new Harness(Managed(port: 16379));

        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Running);

        var files = Directory.GetFiles(harness.DataDirectory);
        var pidFile = Assert.Single(files);
        Assert.Equal(GarnetPidFile.FileName, Path.GetFileName(pidFile));
        Assert.Equal("4242", File.ReadAllText(pidFile));
        Assert.DoesNotContain(harness.Connection.PasswordForChild(), File.ReadAllText(pidFile), StringComparison.Ordinal);

        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(pidFile));
    }

    [Fact]
    public async Task Crash_marks_the_connection_unavailable_then_restarts_after_the_wait()
    {
        var harness = new Harness(Managed(port: 16379));
        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Running);

        harness.Launcher.Children[0].Crash();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Backoff);
        Assert.False(harness.Connection.IsReady);

        await AdvanceUntilAsync(harness.Clock, () => harness.Launcher.Launches.Count == 2 && harness.Supervisor.State == ValkeyChildState.Running);

        Assert.Equal(1, harness.Supervisor.Restarts);
        Assert.DoesNotContain("-r", harness.Launcher.Launches[0].Arguments);
        Assert.Contains("-r", harness.Launcher.Launches[1].Arguments);
        Assert.True(harness.Launcher.Children[0].Killed, "the crashed child is killed and disposed before the restart");
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Circuit_breaker_opens_after_max_failures_and_stays_open()
    {
        var harness = new Harness(Managed(port: 16379), launchFailure: new InvalidOperationException("binary not found"), maxFailures: 3);

        await harness.StartAsync();
        await AdvanceUntilAsync(harness.Clock, () => harness.Supervisor.State == ValkeyChildState.Tripped);

        Assert.Equal(3, harness.Launcher.Launches.Count);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, harness.Launcher.Launches.Count);
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ValkeyChildState.Tripped, harness.Supervisor.State);
    }

    [Fact]
    public async Task Failed_launch_is_a_state_and_never_escapes_the_host()
    {
        var harness = new Harness(Managed(port: 16379), launchFailure: new InvalidOperationException("boom"));

        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Backoff);

        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ValkeyChildState.Stopped, harness.Supervisor.State);
    }

    [Fact]
    public async Task Child_that_never_answers_is_killed_and_counted_as_a_failure()
    {
        var harness = new Harness(Managed(port: 16379), answering: false);

        await harness.StartAsync();
        await AdvanceUntilAsync(harness.Clock, () => harness.Supervisor.State == ValkeyChildState.Backoff);

        Assert.NotEmpty(harness.Launcher.Children);
        Assert.All(harness.Launcher.Children, child => Assert.True(child.Killed));
        Assert.False(harness.Connection.IsReady);
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_stop_kills_the_running_child_removes_the_pid_file_and_reports_stopped()
    {
        var harness = new Harness(Managed(port: 16379));
        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Running);

        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ValkeyChildState.Stopped, harness.Supervisor.State);
        Assert.True(Assert.Single(harness.Launcher.Children).Killed);
        Assert.Single(harness.Launcher.Launches);
        Assert.False(harness.Connection.IsReady);
        Assert.False(File.Exists(Path.Combine(harness.DataDirectory, GarnetPidFile.FileName)));
    }

    [Fact]
    public async Task A_stale_child_from_an_earlier_host_is_swept_before_the_first_start()
    {
        var harness = new Harness(Managed(port: 16379));
        Directory.CreateDirectory(harness.DataDirectory);
        File.WriteAllText(Path.Combine(harness.DataDirectory, GarnetPidFile.FileName), "777");
        harness.Probe.Named.Add(777);

        await harness.StartAsync();
        await WaitUntilAsync(() => harness.Supervisor.State == ValkeyChildState.Running);

        Assert.Equal([777], harness.Probe.Killed);
        await harness.Supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(20, 60)]
    public void Backoff_doubles_from_the_base_and_is_capped(int failures, double expectedSeconds)
    {
        var harness = new Harness(Managed(port: 16379));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), harness.Supervisor.BackoffFor(failures));
    }

    private static GarnetCacheOptions Managed(int port) => new() { Managed = true, BinaryPath = "Garnet.exe", ManagedPort = port };

    /// <summary>Polls real time until the condition holds. Used where the supervisor runs on its own task.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not reached in time");
    }

    /// <summary>Moves fake time forward in small steps until the condition holds, so each timer the supervisor registers can fire.</summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            time.Advance(_step);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not reached in time");
    }

    /// <summary>Wires the supervisor with fakes and a private runtime directory.</summary>
    private sealed class Harness
    {
        public Harness(GarnetCacheOptions options, Exception? launchFailure = null, bool answering = true, int maxFailures = 5)
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "bazaar-garnet-test-" + Guid.NewGuid().ToString("N"));
            options.DataDirectory = DataDirectory;
            Clock = new FakeTimeProvider();
            Launcher = new FakeLauncher { LaunchFailure = launchFailure };
            Factory = new FakeFactory { Answering = answering };
            Connection = new GarnetManagedConnection();
            Probe = new FakeProbe();
            Supervisor = new GarnetServerSupervisor(
                options,
                new GarnetSupervisionOptions { MaxFailures = maxFailures },
                Launcher,
                Factory,
                Clock,
                NullLogger<GarnetServerSupervisor>.Instance,
                Connection,
                new GarnetStaleChildSweeper(Probe));
        }

        public string DataDirectory { get; }

        public FakeTimeProvider Clock { get; }

        public FakeLauncher Launcher { get; }

        public FakeFactory Factory { get; }

        public GarnetManagedConnection Connection { get; }

        public FakeProbe Probe { get; }

        public GarnetServerSupervisor Supervisor { get; }

        public Task StartAsync() => Supervisor.StartAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FakeLauncher : IGarnetProcessLauncher
    {
        public List<GarnetLaunchSpec> Launches { get; } = [];

        public List<FakeChild> Children { get; } = [];

        public Exception? LaunchFailure { get; init; }

        public Task<IValkeyChild> StartAsync(GarnetLaunchSpec spec, CancellationToken ct)
        {
            Launches.Add(spec);
            if (LaunchFailure is not null)
            {
                throw LaunchFailure;
            }

            var child = new FakeChild();
            Children.Add(child);
            return Task.FromResult<IValkeyChild>(child);
        }
    }

    private sealed class FakeChild : IValkeyChild
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 4242;

        public Task Exited => _exit.Task;

        public bool Killed { get; private set; }

        /// <summary>Simulates the server dying on its own.</summary>
        public void Crash() => _exit.TrySetResult();

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IValkeyClientFactory
    {
        public bool Answering { get; init; } = true;

        public List<string> Endpoints { get; } = [];

        public IValkeyClient Create(string connectionString)
        {
            Endpoints.Add(connectionString);
            return new PingClient(this);
        }

        private sealed class PingClient(FakeFactory owner) : IValkeyClient
        {
            public Task PingAsync(CancellationToken ct) =>
                owner.Answering ? Task.CompletedTask : Task.FromException(new IOException("connection refused"));

            public Task<byte[]?> GetAsync(string key, CancellationToken ct) => throw new NotSupportedException();

            public Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();

            public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();

            public Task<ValkeyMemoryInfo> MemoryInfoAsync(CancellationToken ct) => throw new NotSupportedException();

            public Task<string?> GetConfigAsync(string parameter, CancellationToken ct) => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProbe : IGarnetProcessProbe
    {
        public HashSet<int> Named { get; } = [];

        public List<int> Killed { get; } = [];

        public bool IsNamed(int pid, string name) => Named.Contains(pid);

        public void Kill(int pid) => Killed.Add(pid);
    }
}
