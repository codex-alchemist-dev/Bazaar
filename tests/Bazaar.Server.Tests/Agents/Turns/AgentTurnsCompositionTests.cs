using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Tests.Agents.Turns.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>
/// The production turn-path composition: the dispatcher resolves with its policy engine, and the Ryusei capabilities are the
/// only ones the policy knows. The stores here are fakes; their Postgres versions are covered by their own suites.
/// </summary>
public sealed class AgentTurnsCompositionTests
{
    [Fact]
    public void The_dispatcher_resolves_with_no_tools_and_no_capabilities()
    {
        using var provider = Build(withRyusei: false);

        Assert.IsAssignableFrom<IToolDispatcher>(provider.GetRequiredService<IToolDispatcher>());
        Assert.Empty(provider.GetRequiredService<CapabilityRegistry>().Tools);
    }

    [Fact]
    public void The_ryusei_capabilities_carry_the_policy_tiers_from_the_design()
    {
        using var provider = Build(withRyusei: true);
        var registry = provider.GetRequiredService<CapabilityRegistry>();

        Assert.True(registry.TryGet("ryusei.list", out var list));
        Assert.Equal(ToolTier.Autonomous, list.Tier);
        Assert.True(registry.TryGet("ryusei.schedule", out var schedule));
        Assert.Equal(ToolTier.AutonomousWithNotice, schedule.Tier);
        Assert.Equal(SessionRoles.Actor, schedule.Roles);
        Assert.True(registry.TryGet("ryusei.pause", out _));
        Assert.True(registry.TryGet("ryusei.cancel", out _));
    }

    [Fact]
    public void An_unregistered_tool_is_denied_by_the_policy_engine()
    {
        using var provider = Build(withRyusei: true);
        var engine = provider.GetRequiredService<IToolPolicyEngine>();

        var decision = engine.Evaluate(new DispatchContext(
            AgentId: "agent-1",
            SessionId: Guid.NewGuid(),
            Role: SessionRoles.Actor,
            Tool: "shell.exec",
            Platform: null,
            Target: null,
            Args: System.Text.Json.JsonElement.Parse("{}"),
            Taint: SessionTaint.Clean,
            Now: DateTimeOffset.UtcNow,
            Grant: null));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    [Fact]
    public async Task The_session_taint_reads_resolve_over_the_store_and_one_directory()
    {
        using var provider = Build(withRyusei: false);
        var agent = Guid.NewGuid().ToString("D");
        var session = Guid.NewGuid();

        Assert.Same(provider.GetRequiredService<ISessionTaintStore>(), provider.GetRequiredService<ISessionTaintReader>());
        Assert.Same(provider.GetRequiredService<IAgentSessionDirectory>(), provider.GetRequiredService<IAgentSessionRegistrar>());

        provider.GetRequiredService<IAgentSessionRegistrar>().BeginSession(agent, session);
        var reader = provider.GetRequiredService<IAgentSessionTaintReader>();

        // The fake store reports Clean for every session; a registered session is read through it.
        Assert.Equal(TaintState.Clean, await reader.GetActiveTaintAsync(agent, TestContext.Current.CancellationToken));
        Assert.Equal([session], await provider.GetRequiredService<IAgentSessionDirectory>().ListActiveAsync(agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_hard_tainted_store_makes_a_registered_agent_hard()
    {
        using var provider = Build(withRyusei: false, Security.Policy.PolicyFixtures.Hard());
        var agent = Guid.NewGuid().ToString("D");
        var reader = provider.GetRequiredService<IAgentSessionTaintReader>();

        Assert.Equal(TaintState.Clean, await reader.GetActiveTaintAsync(agent, TestContext.Current.CancellationToken));

        provider.GetRequiredService<IAgentSessionRegistrar>().BeginSession(agent, Guid.NewGuid());

        Assert.Equal(TaintState.Hard, await reader.GetActiveTaintAsync(agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_production_turn_runner_registers_each_run_in_the_session_directory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITurnContextAssembler>(new Runner.FakeContextAssembler());
        services.AddSingleton<Bazaar.Server.Gateway.IModelGateway>(new ScriptedModelGateway());
        services.AddSingleton(Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused"));
        using var provider = Build(withRyusei: false, services: services);

        Assert.IsType<SessionRegisteringTurnRunner>(provider.GetRequiredService<ITurnRunner>());
    }

    private static ServiceProvider Build(bool withRyusei, SessionTaint? taint = null, ServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        services.AddSingleton<ISessionTaintStore>(new FakeSessionTaintStore(taint ?? SessionTaint.Clean));
        services.AddSingleton<IInFlightCallStore>(new FakeInFlightCallStore());
        services.AddSingleton<IAuditLog>(new NullAuditLog());
        services.AddSingleton(TimeProvider.System);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(withRyusei
                ? new Dictionary<string, string?> { ["Bazaar:Ryusei:ProjectId"] = Guid.NewGuid().ToString() }
                : new Dictionary<string, string?>())
            .Build();
        new AgentTurnsComponent().AddServices(services, configuration);
        if (withRyusei)
        {
            // The Ryusei tool factories need the trigger service and the database, so these tests never resolve the
            // dispatcher in that case. They read the capabilities and the policy, whose registrations need no database.
            new RyuseiScheduleComponent().AddServices(services, configuration);
        }

        return services.BuildServiceProvider();
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
