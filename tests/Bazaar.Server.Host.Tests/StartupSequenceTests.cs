using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Identity.Kek;
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

    private sealed class FakeCustody(KekCustodyResult result) : IKekCustodyCheck
    {
        public int Calls { get; private set; }

        public Task<KekCustodyResult> CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static (StartupSequence Sequence, HostState State, HostExitCode Exit, FakeLifetime Lifetime) Create(
        FakeLock instanceLock, ISchemaGate gate, ProviderRole[]? required = null, IKekCustodyCheck? custody = null)
    {
        var state = new HostState(TimeProvider.System);
        var exit = new HostExitCode();
        var lifetime = new FakeLifetime();
        var options = Options.Create(new BazaarReadinessOptions { RequiredRoles = [.. required ?? []] });
        var report = new ProviderCapabilityReport([ProviderSelector.Select(ProviderRole.Cache, ProviderSelector.None, Array.Empty<ICacheProvider>())]);
        return (new StartupSequence(instanceLock, gate, report, options, state, exit, lifetime, NullLogger<StartupSequence>.Instance, custody), state, exit, lifetime);
    }

    // Chaos finding: a host started against an unmounted data root minted a new key file, ran, and failed every encrypted
    // read and write while reporting ready.
    [Fact]
    public async Task A_key_file_that_opens_none_of_the_stored_keys_refuses_the_start()
    {
        var custody = new FakeCustody(new KekCustodyResult(Stored: 12, Opened: 0));
        var (sequence, state, exit, lifetime) = Create(new FakeLock(acquire: true), new FakeGate(), custody: custody);

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("does not open the keys stored in this database", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Bazaar:Host:DataRoot", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, exit.Value);
        Assert.True(lifetime.Stopped);
        Assert.False(state.Started);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task A_first_start_or_a_matching_key_file_starts(int stored, int opened)
    {
        var custody = new FakeCustody(new KekCustodyResult(stored, opened));
        var (sequence, state, exit, _) = Create(new FakeLock(acquire: true), new FakeGate(), custody: custody);

        await sequence.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, custody.Calls);
        Assert.True(state.Started);
        Assert.Equal(0, exit.Value);
    }

    [Fact]
    public async Task The_key_check_does_not_run_when_the_schema_is_refused()
    {
        var custody = new FakeCustody(new KekCustodyResult(0, 0));
        var (sequence, _, _, _) = Create(new FakeLock(acquire: true), new FakeGate(new SchemaVersionException("newer")), custody: custody);

        await Assert.ThrowsAsync<StartupRefusedException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, custody.Calls);
    }

    [Fact]
    public async Task Second_instance_stops_the_host_with_exit_code_1_and_skips_the_rest()
    {
        var gate = new FakeGate();
        var (sequence, state, exit, lifetime) = Create(new FakeLock(acquire: false), gate);

        await Assert.ThrowsAsync<StartupRefusedException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

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
    public async Task A_migration_refusal_stops_the_host_with_exit_code_1_and_never_marks_ready()
    {
        var (sequence, state, exit, lifetime) = Create(new FakeLock(acquire: true), new FakeGate(new SchemaVersionException("newer")));

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("newer", refused.Message);
        Assert.Equal(1, exit.Value);
        Assert.True(lifetime.Stopped);
        Assert.False(state.Started);
    }

    [Fact]
    public async Task An_unreachable_database_refuses_with_a_description_instead_of_crashing()
    {
        var (sequence, state, exit, _) = Create(new FakeLock(acquire: true), new FakeGate(new Npgsql.NpgsqlException("boom", new IOException("Connection refused"))));

        var refused = await Assert.ThrowsAsync<StartupRefusedException>(() => sequence.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cannot reach the database", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, exit.Value);
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
