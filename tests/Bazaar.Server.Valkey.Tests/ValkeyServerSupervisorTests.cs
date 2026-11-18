using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Valkey.Tests;

/// <summary>The managed child's lifecycle: start, answer, crash, back off, trip the breaker and stop. Time is fake.</summary>
public class ValkeyServerSupervisorTests
{
    private static readonly TimeSpan _step = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Unmanaged_cache_is_disabled_and_launches_nothing()
    {
        var launcher = new FakeLauncher();
        var supervisor = Create(new ValkeyCacheOptions { Managed = false }, launcher, new FakeFactory(), new FakeTimeProvider());

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ValkeyChildState.Disabled, supervisor.State);
        Assert.Empty(launcher.Launches);
        await supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Managed_child_starts_on_loopback_and_is_running_once_it_answers()
    {
        var launcher = new FakeLauncher();
        var factory = new FakeFactory();
        var supervisor = Create(Managed(), launcher, factory, new FakeTimeProvider());

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => supervisor.State == ValkeyChildState.Running);

        var spec = Assert.Single(launcher.Launches);
        Assert.Equal("valkey-server", spec.Executable);
        Assert.Equal(16379, spec.Port);
        Assert.Equal("127.0.0.1:16379", Assert.Single(factory.Endpoints));
        Assert.Equal(0, supervisor.Restarts);
        await supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Crash_moves_to_backoff_and_restarts_after_the_wait()
    {
        var time = new FakeTimeProvider();
        var launcher = new FakeLauncher();
        var supervisor = Create(Managed(), launcher, new FakeFactory(), time);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => supervisor.State == ValkeyChildState.Running);

        launcher.Children[0].Crash();
        await WaitUntilAsync(() => supervisor.State == ValkeyChildState.Backoff);
        await AdvanceUntilAsync(time, () => launcher.Launches.Count == 2 && supervisor.State == ValkeyChildState.Running);

        Assert.Equal(1, supervisor.Restarts);
        Assert.True(launcher.Children[0].Killed, "the crashed child is killed and disposed before the restart");
        await supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Circuit_breaker_opens_after_max_failures_and_stays_open()
    {
        var time = new FakeTimeProvider();
        var launcher = new FakeLauncher { LaunchFailure = new InvalidOperationException("binary not found") };
        var options = Managed();
        var supervision = new ValkeySupervisionOptions { MaxFailures = 3 };
        var supervisor = new ValkeyServerSupervisor(options, supervision, launcher, new FakeFactory(), time, NullLogger<ValkeyServerSupervisor>.Instance);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await AdvanceUntilAsync(time, () => supervisor.State == ValkeyChildState.Tripped);

        Assert.Equal(3, launcher.Launches.Count);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, launcher.Launches.Count);
        Assert.Equal(ValkeyChildState.Tripped, supervisor.State);
        await supervisor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ValkeyChildState.Tripped, supervisor.State);
    }

    [Fact]
    public async Task Failed_launch_is_a_state_and_never_escapes_the_host()
    {
        var time = new FakeTimeProvider();
        var launcher = new FakeLauncher { LaunchFailure = new InvalidOperationException("boom") };
        var supervisor = Create(Managed(), launcher, new FakeFactory(), time);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => supervisor.State == ValkeyChildState.Backoff);

        await supervisor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ValkeyChildState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task Child_that_never_answers_is_killed_and_counted_as_a_failure()
    {
        var time = new FakeTimeProvider();
        var launcher = new FakeLauncher();
        var factory = new FakeFactory { Answering = false };
        var supervisor = Create(Managed(), launcher, factory, time);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await AdvanceUntilAsync(time, () => supervisor.State == ValkeyChildState.Backoff);

        Assert.NotEmpty(launcher.Children);
        Assert.All(launcher.Children, child => Assert.True(child.Killed));
        await supervisor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_stop_kills_the_running_child_and_reports_stopped()
    {
        var launcher = new FakeLauncher();
        var supervisor = Create(Managed(), launcher, new FakeFactory(), new FakeTimeProvider());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => supervisor.State == ValkeyChildState.Running);

        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ValkeyChildState.Stopped, supervisor.State);
        Assert.True(Assert.Single(launcher.Children).Killed);
        Assert.Single(launcher.Launches);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(20, 60)]
    public void Backoff_doubles_from_the_base_and_is_capped(int failures, double expectedSeconds)
    {
        var supervisor = Create(Managed(), new FakeLauncher(), new FakeFactory(), new FakeTimeProvider());

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), supervisor.BackoffFor(failures));
    }

    private static ValkeyCacheOptions Managed() => new() { Managed = true, BinaryPath = "valkey-server", ManagedPort = 16379 };

    private static ValkeyServerSupervisor Create(
        ValkeyCacheOptions options,
        IValkeyProcessLauncher launcher,
        IValkeyClientFactory factory,
        TimeProvider clock) =>
        new(options, new ValkeySupervisionOptions(), launcher, factory, clock, NullLogger<ValkeyServerSupervisor>.Instance);

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

    private sealed class FakeLauncher : IValkeyProcessLauncher
    {
        public List<ValkeyLaunchSpec> Launches { get; } = [];

        public List<FakeChild> Children { get; } = [];

        public Exception? LaunchFailure { get; init; }

        public Task<IValkeyChild> StartAsync(ValkeyLaunchSpec spec, CancellationToken ct)
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

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
