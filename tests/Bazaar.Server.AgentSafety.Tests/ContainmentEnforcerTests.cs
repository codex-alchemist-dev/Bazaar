using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class ContainmentEnforcerTests
{
    private static readonly TimeSpan _fast = TimeSpan.FromMilliseconds(5);

    private readonly FakeStore _store = new();
    private readonly FakeRevoker _revoker = new();
    private readonly FakePauser _pauser = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ContainmentEnforcer Create() =>
        new(_store, _revoker, _pauser, NullLogger<ContainmentEnforcer>.Instance, TimeProvider.System, _fast);

    [Fact]
    public async Task Every_contained_agent_is_revoked_and_paused_again()
    {
        _store.Contained = ["a1", "a2"];

        var failed = await Create().EnforceOnceAsync(Ct);

        Assert.NotNull(failed);
        Assert.Empty(failed);
        Assert.Equal<string>(["a1", "a2"], _revoker.Revoked);
        Assert.Equal<string>(["a1", "a2"], _pauser.Paused);
    }

    [Fact]
    public async Task With_nobody_contained_nothing_is_touched()
    {
        var failed = await Create().EnforceOnceAsync(Ct);

        Assert.Empty(failed!);
        Assert.Empty(_revoker.Revoked);
        Assert.Empty(_pauser.Paused);
    }

    [Fact]
    public async Task A_failing_revoker_does_not_stop_the_pause_and_the_agent_is_reported()
    {
        _store.Contained = ["a1"];
        _revoker.FailFor.Add("a1");

        var failed = await Create().EnforceOnceAsync(Ct);

        Assert.Equal<string>(["a1"], failed!);
        Assert.Equal<string>(["a1"], _pauser.Paused);
    }

    [Fact]
    public async Task A_failing_pauser_does_not_stop_the_revoke_and_one_bad_agent_does_not_hide_the_next()
    {
        _store.Contained = ["a1", "a2"];
        _pauser.FailFor.Add("a1");

        var failed = await Create().EnforceOnceAsync(Ct);

        Assert.Equal<string>(["a1"], failed!);
        Assert.Equal<string>(["a1", "a2"], _revoker.Revoked);
        Assert.Equal<string>(["a2"], _pauser.Paused);
    }

    [Fact]
    public async Task An_unreadable_store_is_reported_as_null_not_as_nobody_contained()
    {
        _store.Unreadable = true;

        Assert.Null(await Create().EnforceOnceAsync(Ct));
    }

    [Fact]
    public async Task The_service_retries_failed_agents_until_they_are_contained()
    {
        _store.Contained = ["a1"];
        _pauser.FailFor.Add("a1");
        var enforcer = Create();

        await enforcer.StartAsync(Ct);
        await WaitForAsync(() => _pauser.Attempts >= 3);
        _pauser.FailFor.Clear();
        await WaitForAsync(() => _pauser.Paused.Contains("a1"));
        await enforcer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await enforcer.StopAsync(Ct);

        Assert.Contains("a1", _pauser.Paused);
    }

    [Fact]
    public async Task The_service_retries_when_the_store_comes_back_and_a_released_agent_drops_out()
    {
        _store.Contained = ["a1"];
        _store.Unreadable = true;
        var enforcer = Create();

        await enforcer.StartAsync(Ct);
        await WaitForAsync(() => _store.LoadAttempts >= 3);
        _store.Contained = [];
        _store.Unreadable = false;
        await enforcer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await enforcer.StopAsync(Ct);

        Assert.Empty(_pauser.Paused);
    }

    [Fact]
    public async Task Stopping_the_host_ends_the_retry_loop()
    {
        _store.Unreadable = true;
        var enforcer = Create();

        await enforcer.StartAsync(Ct);
        await enforcer.StopAsync(Ct);

        Assert.True(enforcer.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public void The_retry_interval_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContainmentEnforcer(_store, _revoker, _pauser, NullLogger<ContainmentEnforcer>.Instance, TimeProvider.System, TimeSpan.Zero));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(2, Ct);
        }
    }

    private sealed class FakeStore : IContainmentStateStore
    {
        private int _loads;

        public IReadOnlyList<string> Contained { get; set; } = [];

        public bool Unreadable { get; set; }

        public int LoadAttempts => Volatile.Read(ref _loads);

        public Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loads);
            if (Unreadable)
            {
                throw new InvalidOperationException("store down");
            }

            return Task.FromResult<IReadOnlyList<ContainedAgent>>(Contained.Select(id => new ContainedAgent(id, DateTimeOffset.UnixEpoch, "owner")).ToList());
        }

        public Task AddAsync(ContainedAgent agent, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveAsync(string agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeRevoker : IAgentTokenRevoker
    {
        public List<string> Revoked { get; } = [];

        public HashSet<string> FailFor { get; } = [];

        public Task RevokeAsync(string agentId, CancellationToken ct = default)
        {
            Revoked.Add(agentId);
            return FailFor.Contains(agentId) ? throw new InvalidOperationException("revoke failed") : Task.CompletedTask;
        }

        public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePauser : IVmPauser
    {
        private int _attempts;

        public List<string> Paused { get; } = [];

        public HashSet<string> FailFor { get; } = [];

        public int Attempts => Volatile.Read(ref _attempts);

        public Task PauseAsync(string agentId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            if (FailFor.Contains(agentId))
            {
                throw new InvalidOperationException("pause failed");
            }

            lock (Paused)
            {
                Paused.Add(agentId);
            }

            return Task.CompletedTask;
        }

        public Task ResumeAsync(string agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
