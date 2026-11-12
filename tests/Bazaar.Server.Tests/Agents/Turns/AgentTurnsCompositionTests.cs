using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Kanban.Ryusei;
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

    private static ServiceProvider Build(bool withRyusei)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionTaintStore>(new FakeSessionTaintStore(SessionTaint.Clean));
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
