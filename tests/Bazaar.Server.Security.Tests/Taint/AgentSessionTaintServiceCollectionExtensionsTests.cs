using Bazaar.Approvals;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Security.Tests.KillSwitch;
using Bazaar.Server.Security.Tests.Quarantine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Security.Tests.Taint;

public class AgentSessionTaintServiceCollectionExtensionsTests
{
    private static readonly Guid _session = Guid.Parse("0196a6f0-0000-7000-8000-000000000b01");

    [Fact]
    public async Task The_registrar_and_the_directory_are_one_instance_that_feeds_the_agent_reader()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionTaintReader>(new HardReader());
        services.AddAgentSessionTaint();
        using var provider = services.BuildServiceProvider();

        var registrar = provider.GetRequiredService<IAgentSessionRegistrar>();
        Assert.Same(registrar, provider.GetRequiredService<IAgentSessionDirectory>());
        Assert.Same(provider.GetRequiredService<IAgentSessionTaintReader>(), provider.GetRequiredService<IAgentSessionTaintReader>());

        var reader = provider.GetRequiredService<IAgentSessionTaintReader>();
        Assert.Equal(TaintState.Clean, await reader.GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));

        registrar.BeginSession("agent-a", _session);
        Assert.Equal(TaintState.Hard, await reader.GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));

        registrar.EndSession("agent-a", _session);
        Assert.Equal(TaintState.Clean, await reader.GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Calling_it_twice_keeps_one_directory()
    {
        var services = new ServiceCollection();
        services.AddAgentSessionTaint();
        services.AddAgentSessionTaint();
        using var provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(IAgentSessionRegistrar));
        Assert.Same(provider.GetRequiredService<IAgentSessionRegistrar>(), provider.GetRequiredService<IAgentSessionDirectory>());
    }

    [Fact]
    public void AddAgentSafety_registers_the_session_taint_reads()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(new RecordingAuditLog());
        services.AddSingleton<IOwnerKeyRegistry>(new InMemoryOwnerKeyRegistry());
        services.AddSingleton<IVmPauser>(new RecordingVmPauser());
        services.AddSingleton<IAgentTokenRevoker>(new RecordingAgentTokenRevoker());
        services.AddSingleton<ISessionTaintReader>(new HardReader());
        services.AddAgentSafety(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryAgentSessionDirectory>(provider.GetRequiredService<IAgentSessionRegistrar>());
        Assert.IsType<AgentSessionTaintReader>(provider.GetRequiredService<IAgentSessionTaintReader>());
    }

    private sealed class HardReader : ISessionTaintReader
    {
        public Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(TaintRules.Apply(SessionTaint.Clean, new TaintInput(Guid.NewGuid(), ContentTrust.External), TaintPolicy.Default));
    }
}
