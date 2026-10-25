using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class StartupSequenceTests
{
    private sealed class FakeLock(bool acquire) : IInstanceLock
    {
        public bool IsHeld { get; private set; }

        public bool Disposed { get; private set; }

        public Task<bool> TryAcquireAsync(CancellationToken ct)
        {
            IsHeld = acquire;
            return Task.FromResult(acquire);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsHeld = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGate(Exception? failure = null) : ISchemaGate
    {
        public int Calls { get; private set; }

        public Task EnsureCurrentAsync(CancellationToken ct)
        {
            Calls++;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }

    private static (StartupSequence Sequence, HostState State, HostExitCode Exit, FakeLifetime Lifetime) Create(
        FakeLock instanceLock, ISchemaGate gate, ProviderRole[]? required = null)
    {
        var state = new HostState(TimeProvider.System);
        var exit = new HostExitCode();
        var lifetime = new FakeLifetime();
        var options = Options.Create(new BazaarReadinessOptions { RequiredRoles = [.. required ?? []] });
        var report = new ProviderCapabilityReport([ProviderSelector.Select(ProviderRole.Cache, ProviderSelector.None, Array.Empty<ICacheProvider>())]);
        return (new StartupSequence(instanceLock, gate, report, options, state, exit, lifetime, NullLogger<StartupSequence>.Instance), state, exit, lifetime);
    }

    [Fact]
    public async Task Second_instance_stops_the_host_with_exit_code_1_and_skips_the_rest()
    {
        var gate = new FakeGate();
        var (sequence, state, exit, lifetime) = Create(new FakeLock(acquire: false), gate);

        await sequence.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(lifetime.Stopped);
        Assert.Equal(1, exit.Value);
        Assert.Equal(0, gate.Calls);
        Assert.False(state.Started);
    }

    [Fact]
    public async Task Successful_start_marks_started_after_the_schema_check()
    {
        var gate = new FakeGate();
        var (sequence, state, exit, lifetime) = Create(new FakeLock(acquire: true), gate);

        await sequence.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(state.Started);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(0, exit.Value);
        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public async Task Schema_mismatch_fails_the_start_and_never_marks_ready()
    {
        var (sequence, state, _, _) = Create(new FakeLock(acquire: true), new FakeGate(new InvalidOperationException("schema newer")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

        Assert.False(state.Started);
    }

    [Fact]
    public async Task Missing_required_provider_role_refuses_to_start()
    {
        var (sequence, state, _, _) = Create(new FakeLock(acquire: true), new FakeGate(), [ProviderRole.Cache]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

        Assert.False(state.Started);
    }

    [Fact]
    public async Task Stop_releases_the_lock()
    {
        var instanceLock = new FakeLock(acquire: true);
        var (sequence, _, _, _) = Create(instanceLock, new FakeGate());

        await sequence.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(instanceLock.Disposed);
    }
}
