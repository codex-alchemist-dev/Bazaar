using Bazaar.Server.Agents.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Tests.Agents.FirstRun;

public class AgentGrantRepairTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reports the agents in the set as having no policy row.</summary>
    private sealed class MissingPolicyPresence(params Guid[] withoutPolicy) : IAgentPolicyPresence
    {
        public Task<IReadOnlyList<Guid>> FilterWithoutPolicyAsync(IEnumerable<Guid> agentIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(agentIds.Where(withoutPolicy.Contains).ToList());
    }

    private sealed class RecordingGrant(params Guid[] failing) : IAgentDefaultPolicyGrant
    {
        public List<Guid> Granted { get; } = [];

        public Task GrantAsync(Guid agentId, CancellationToken ct)
        {
            if (failing.Contains(agentId))
                throw new InvalidOperationException("policy store unavailable");
            Granted.Add(agentId);
            return Task.CompletedTask;
        }
    }

    private static Func<CancellationToken, Task<IReadOnlyList<Guid>>> Agents(params Guid[] ids) =>
        _ => Task.FromResult<IReadOnlyList<Guid>>(ids);

    [Fact]
    public async Task Repairs_every_agent_the_presence_check_returns()
    {
        var a = Guid.CreateVersion7();
        var healthy = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var grant = new RecordingGrant();
        var repair = new AgentGrantRepair(grant, new MissingPolicyPresence(a, b), Agents(a, healthy, b), NullLogger<AgentGrantRepair>.Instance);

        var repaired = await repair.RepairAsync(Ct);

        Assert.Equal(2, repaired);
        Assert.Equal([a, b], grant.Granted);
    }

    [Fact]
    public async Task One_failing_agent_does_not_stop_the_rest()
    {
        var failing = Guid.CreateVersion7();
        var ok = Guid.CreateVersion7();
        var grant = new RecordingGrant(failing);
        var repair = new AgentGrantRepair(grant, new MissingPolicyPresence(failing, ok), Agents(failing, ok), NullLogger<AgentGrantRepair>.Instance);

        var repaired = await repair.RepairAsync(Ct);

        Assert.Equal(1, repaired);
        Assert.Equal([ok], grant.Granted);
    }

    [Fact]
    public async Task Without_a_presence_service_repair_returns_zero_and_grants_nothing()
    {
        var grant = new RecordingGrant();
        var repair = new AgentGrantRepair(grant, null, Agents(Guid.CreateVersion7()), NullLogger<AgentGrantRepair>.Instance);

        Assert.Equal(0, await repair.RepairAsync(Ct));
        Assert.Empty(grant.Granted);
    }
}
