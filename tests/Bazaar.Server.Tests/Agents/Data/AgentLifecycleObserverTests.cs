using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Data;

public sealed class AgentLifecycleObserverTests(AgentDb db) : IClassFixture<AgentDb>
{
    [Fact]
    public async Task Retirement_observer_runs_only_after_a_successful_transition()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var observer = new RecordingObserver();
        var repository = new AgentRepository(db.DataSource, observer);
        var agentId = await repository.CreateAsync("Retiree", ct: TestContext.Current.CancellationToken);

        Assert.True(await repository.SetStateAsync(
            agentId,
            AgentState.Created,
            AgentState.Retired,
            TestContext.Current.CancellationToken));
        Assert.False(await repository.SetStateAsync(
            agentId,
            AgentState.Created,
            AgentState.Retired,
            TestContext.Current.CancellationToken));

        Assert.Equal([agentId], observer.RetiredAgentIds);
    }

    [Fact]
    public async Task Observer_failure_is_explicit_after_the_state_change()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var observer = new RecordingObserver { Failure = new InvalidOperationException("retirement dispatch failed") };
        var repository = new AgentRepository(db.DataSource, observer);
        var agentId = await repository.CreateAsync("Retiree", ct: TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SetStateAsync(agentId, AgentState.Created, AgentState.Retired, TestContext.Current.CancellationToken));

        Assert.Equal("retirement dispatch failed", error.Message);
        Assert.Equal(AgentState.Retired, (await repository.GetAsync(agentId, TestContext.Current.CancellationToken))!.State);
    }

    private sealed class RecordingObserver : IAgentLifecycleObserver
    {
        public List<Guid> RetiredAgentIds { get; } = [];
        public Exception? Failure { get; init; }

        public Task AgentRetiredAsync(Guid agentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetiredAgentIds.Add(agentId);
            if (Failure is not null)
                throw Failure;
            return Task.CompletedTask;
        }
    }
}
