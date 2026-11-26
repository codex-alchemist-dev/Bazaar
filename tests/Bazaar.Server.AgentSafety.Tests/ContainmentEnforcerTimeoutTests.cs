using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Tests;

/// <summary>A hung adapter call must not stall the pass, and every token cut comes before any VM pause.</summary>
public sealed class ContainmentEnforcerTimeoutTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _log = [];
    private readonly Store _store = new(["a1", "a2"]);

    private ContainmentEnforcer Create(IAgentTokenRevoker revoker, IVmPauser pauser, TimeSpan? stepTimeout = null) =>
        new(_store, revoker, pauser, NullLogger<ContainmentEnforcer>.Instance, _time, null, null, new ContainmentReleaseGuard(), stepTimeout ?? _timeout);

    [Fact]
    public async Task A_revoke_that_ignores_its_token_times_out_is_reported_and_does_not_stall_the_other_agents()
    {
        var revoker = new Adapter(_log, "revoke", hangFor: "a1");
        var pauser = new Adapter(_log, "pause");
        var pass = Create(revoker, pauser).EnforceOnceAsync(Ct);

        await WaitForAsync(() => revoker.Started("a1"));
        Assert.False(pass.IsCompleted);
        _time.Advance(_timeout);
        var failed = await pass;

        Assert.Equal<string>(["a1"], failed!);
        Assert.Contains("pause:a1", _log);
        Assert.Contains("revoke:a2", _log);
        Assert.Contains("pause:a2", _log);
    }

    [Fact]
    public async Task A_hung_pause_times_out_and_is_reported_without_blocking_the_next_agent()
    {
        var revoker = new Adapter(_log, "revoke");
        var pauser = new Adapter(_log, "pause", hangFor: "a1");
        var pass = Create(revoker, pauser).EnforceOnceAsync(Ct);

        await WaitForAsync(() => pauser.Started("a1"));
        _time.Advance(_timeout);
        var failed = await pass;

        Assert.Equal<string>(["a1"], failed!);
        Assert.Contains("pause:a2", _log);
    }

    [Fact]
    public async Task The_step_token_fires_at_the_timeout_so_a_cooperative_adapter_stops()
    {
        var revoker = new Adapter(_log, "revoke", hangFor: "a1", honourToken: true);
        var pass = Create(revoker, new Adapter(_log, "pause")).EnforceOnceAsync(Ct);

        await WaitForAsync(() => revoker.Started("a1"));
        _time.Advance(_timeout);

        Assert.Equal<string>(["a1"], (await pass)!);
    }

    [Fact]
    public async Task Every_revoke_runs_before_any_pause()
    {
        var pass = Create(new Adapter(_log, "revoke"), new Adapter(_log, "pause")).EnforceOnceAsync(Ct);

        Assert.Empty((await pass)!);
        Assert.Equal<string>(["revoke:a1", "revoke:a2", "pause:a1", "pause:a2"], _log);
    }

    [Fact]
    public async Task Cancelling_the_pass_still_aborts_it()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var revoker = new Adapter(_log, "revoke", hangFor: "a1");
        var pass = Create(revoker, new Adapter(_log, "pause")).EnforceOnceAsync(cts.Token);

        await WaitForAsync(() => revoker.Started("a1"));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
    }

    [Fact]
    public void The_step_timeout_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new Adapter(_log, "revoke"), new Adapter(_log, "pause"), TimeSpan.Zero));
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

    private sealed class Store(string[] ids) : IContainmentStateStore
    {
        public Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainedAgent>>([.. ids.Select(id => new ContainedAgent(id, DateTimeOffset.UnixEpoch, "owner"))]);

        public Task AddAsync(ContainedAgent agent, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveAsync(string agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Adapter(List<string> log, string verb, string? hangFor = null, bool honourToken = false) : IAgentTokenRevoker, IVmPauser
    {
        private readonly HashSet<string> _started = [];

        public bool Started(string id)
        {
            lock (_started)
            {
                return _started.Contains(id);
            }
        }

        public Task RevokeAsync(string agentId, CancellationToken ct = default) => RunAsync(agentId, ct);

        public Task PauseAsync(string agentId, CancellationToken ct = default) => RunAsync(agentId, ct);

        public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) => throw new NotSupportedException();

        Task IVmPauser.ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();

        private Task RunAsync(string agentId, CancellationToken ct)
        {
            lock (log)
            {
                log.Add($"{verb}:{agentId}");
            }

            lock (_started)
            {
                _started.Add(agentId);
            }

            if (agentId != hangFor)
            {
                return Task.CompletedTask;
            }

            var never = new TaskCompletionSource();
            if (honourToken)
            {
                ct.Register(() => never.TrySetCanceled(ct));
            }

            return never.Task;
        }
    }
}
