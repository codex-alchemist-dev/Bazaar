using System.Collections.Immutable;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmEgress.Approvals.Security.Tests;

public sealed class AgentTaintReaderAndModuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TaintState.Clean, false)]
    [InlineData(TaintState.Soft, false)]
    [InlineData(TaintState.Hard, true)]
    public async Task Only_hard_taint_counts(TaintState state, bool expected)
    {
        var reader = new AgentTaintReader(new FixedLookup(state));

        Assert.Equal(expected, await reader.IsHardTaintedAsync("agent-1", Ct));
    }

    [Fact]
    public async Task Lookup_failure_propagates()
    {
        var reader = new AgentTaintReader(new ThrowingLookup());

        await Assert.ThrowsAsync<TimeoutException>(async () => await reader.IsHardTaintedAsync("agent-1", Ct));
    }

    [Fact]
    public async Task Missing_lookup_throws()
    {
        var reader = new AgentTaintReader(null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.IsHardTaintedAsync("agent-1", Ct));
    }

    [Fact]
    public async Task Registration_order_does_not_matter_and_wins_over_the_defaults()
    {
        foreach (var adaptersFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAuditLog>(new NullAudit());
            services.AddSingleton<IAgentSessionTaintReader>(new FixedLookup(TaintState.Clean));
            services.AddSingleton<IApprovalStore>(new InMemoryApprovalStore());
            if (adaptersFirst)
            {
                services.AddBazaarVmEgressApprovalsSecurity();
                services.AddBazaarVmEgressApprovals();
            }
            else
            {
                services.AddBazaarVmEgressApprovals();
                services.AddBazaarVmEgressApprovalsSecurity();
            }

            await using var sp = services.BuildServiceProvider();

            Assert.IsType<ApprovalServiceEgressApprovalPort>(sp.GetRequiredService<IEgressApprovalPort>());
            Assert.IsType<AgentTaintReader>(sp.GetRequiredService<IEgressTaintReader>());
            Assert.IsType<EgressApprovalGate>(sp.GetRequiredService<IEgressApprovalGate>());
        }
    }

    [Fact]
    public async Task Gate_denies_when_the_host_has_not_registered_the_approvals_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(new NullAudit());
        services.AddBazaarVmEgressApprovalsSecurity();
        services.AddBazaarVmEgressApprovals();
        await using var sp = services.BuildServiceProvider();

        var state = await sp.GetRequiredService<IEgressApprovalGate>().RequestGrowthAsync("agent-1", ["a.example"], 2, Ct);

        Assert.Equal(EgressApprovalState.Denied, state);
    }

    private sealed class FixedLookup(TaintState state) : IAgentSessionTaintReader
    {
        public ValueTask<TaintState> GetActiveTaintAsync(string agentId, CancellationToken ct = default) => ValueTask.FromResult(state);
    }

    private sealed class ThrowingLookup : IAgentSessionTaintReader
    {
        public ValueTask<TaintState> GetActiveTaintAsync(string agentId, CancellationToken ct = default) => throw new TimeoutException();
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
