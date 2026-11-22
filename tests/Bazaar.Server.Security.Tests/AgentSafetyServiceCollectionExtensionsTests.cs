using Bazaar.Approvals;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Security.Tests.KillSwitch;
using Bazaar.Server.Security.Tests.Quarantine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Security.Tests;

public class AgentSafetyServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    /// <summary>Services the host must register before the safety module, as the registration's remarks say.</summary>
    private static ServiceCollection HostServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(new RecordingAuditLog());
        services.AddSingleton<IOwnerKeyRegistry>(new InMemoryOwnerKeyRegistry());
        services.AddSingleton<IVmPauser>(new RecordingVmPauser());
        services.AddSingleton<IAgentTokenRevoker>(new RecordingAgentTokenRevoker());
        return services;
    }

    [Fact]
    public void AddAgentSafety_resolves_the_kill_switch_engine_and_the_budget_service_as_singletons()
    {
        var services = HostServices();
        services.AddAgentSafety(Configuration());
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<KillSwitchEngine>();
        var budgets = provider.GetRequiredService<ITokenBudgetService>();

        Assert.Same(engine, provider.GetRequiredService<KillSwitchEngine>());
        Assert.Same(budgets, provider.GetRequiredService<ITokenBudgetService>());
        Assert.IsType<TokenBudgetService>(budgets);
    }

    [Fact]
    public void AddAgentSafety_registers_the_audit_and_containment_adapters()
    {
        var services = HostServices();
        services.AddAgentSafety(Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<KillSwitchAuditAdapter>(provider.GetRequiredService<IKillSwitchAuditSink>());
        Assert.IsType<AgentContainmentAdapter>(provider.GetRequiredService<IAgentContainment>());
        Assert.Same(provider.GetRequiredService<IAgentContainment>(), provider.GetRequiredService<IAgentContainment>());
    }

    [Fact]
    public void AddAgentSafety_keeps_a_host_registration_made_before_it()
    {
        var sink = new InMemoryKillSwitchAuditSink();
        var services = HostServices();
        services.AddSingleton<IKillSwitchAuditSink>(sink);

        services.AddAgentSafety(Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.Same(sink, provider.GetRequiredService<IKillSwitchAuditSink>());
    }

    [Fact]
    public void AddAgentSafety_binds_the_budget_options_from_the_named_section()
    {
        var services = HostServices();
        services.AddAgentSafety(Configuration(("Bazaar:Safety:Budgets:HourlyTokens", "5")));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(5, provider.GetRequiredService<TokenBudgetOptions>().HourlyTokens);
    }

    [Fact]
    public void AddAgentSafety_does_not_bind_budget_options_from_root_keys()
    {
        var services = HostServices();
        services.AddAgentSafety(Configuration(("HourlyTokens", "5")));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(200_000, provider.GetRequiredService<TokenBudgetOptions>().HourlyTokens);
    }

    [Fact]
    public void AddAgentSafety_needs_the_audit_log_and_containment_hooks_from_the_host()
    {
        var services = new ServiceCollection();
        services.AddAgentSafety(Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<KillSwitchEngine>());
    }

    [Fact]
    public void AddAgentSafety_rejects_a_null_services_or_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentSafety(Configuration()));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentSafety(null!));
    }
}
