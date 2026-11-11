using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>What <see cref="AgentTurnsComponent"/> registers for the wake loop, off and on.</summary>
public sealed class AgentSchedulerRegistrationTests
{
    private static ServiceCollection Compose(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create($"{AgentSchedulerOptions.SectionName}:{s.Key}", (string?)s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar"));
        services.AddBazaarComponents(configuration, [new AgentTurnsComponent()]);
        return services;
    }

    private static bool Registers<T>(ServiceCollection services) => services.Any(d => d.ServiceType == typeof(T));

    private static bool RegistersHostedLoop(ServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentSchedulerService));

    [Fact]
    public async Task Off_by_default_registers_only_the_options()
    {
        var services = Compose();

        Assert.False(Registers<IAgentScheduler>(services));
        Assert.False(Registers<IDueAgentReader>(services));
        Assert.False(RegistersHostedLoop(services));

        await using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<AgentSchedulerOptions>().Enabled);
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void Disabled_with_other_settings_still_registers_no_scheduler()
    {
        var services = Compose(("Enabled", "false"), ("Route", "claude"));

        Assert.False(Registers<IAgentScheduler>(services));
        Assert.False(Registers<IDueAgentReader>(services));
        Assert.False(RegistersHostedLoop(services));
    }

    [Fact]
    public async Task Enabled_registers_the_reader_the_scheduler_and_the_hosted_loop_with_the_bound_options()
    {
        var services = Compose(
            ("Enabled", "true"),
            ("Route", "claude"),
            ("PollInterval", "00:00:05"),
            ("MaxConcurrentTurns", "2"),
            ("GuardTripBackoff", "00:01:00"),
            ("ToolAllowlist:0", "gc.post"));
        services.AddSingleton<ITurnRunner>(new FakeTurnRunner());
        services.AddSingleton<ILoopGuard>(new FakeLoopGuard());

        Assert.True(RegistersHostedLoop(services));

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AgentSchedulerOptions>();
        Assert.True(options.Enabled);
        Assert.Equal("claude", options.Route);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PollInterval);
        Assert.Equal(2, options.MaxConcurrentTurns);
        Assert.Equal(TimeSpan.FromMinutes(1), options.GuardTripBackoff);
        Assert.Equal(["gc.post"], options.ToolAllowlist);
        Assert.IsType<InboxDueAgentReader>(provider.GetRequiredService<IDueAgentReader>());
        Assert.IsType<InboxAgentScheduler>(provider.GetRequiredService<IAgentScheduler>());
        Assert.IsType<AgentSchedulerService>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    public async Task A_scheduler_registered_first_replaces_the_inbox_scheduler()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                KeyValuePair.Create($"{AgentSchedulerOptions.SectionName}:Enabled", (string?)"true"),
                KeyValuePair.Create($"{AgentSchedulerOptions.SectionName}:Route", (string?)"claude"),
            ])
            .Build();
        var other = new OtherScheduler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentScheduler>(other);
        services.AddBazaarComponents(configuration, [new AgentTurnsComponent()]);

        await using var provider = services.BuildServiceProvider();

        Assert.Same(other, provider.GetRequiredService<IAgentScheduler>());
        Assert.IsType<AgentSchedulerService>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enabled_with_a_blank_route_fails_at_composition(string route)
    {
        var ex = Assert.Throws<ArgumentException>(() => Compose(("Enabled", "true"), ("Route", route)));

        Assert.Contains("Bazaar:AgentTurns:Scheduler:Route", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_range_limit_fails_at_composition_even_while_disabled()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compose(("MaxConcurrentTurns", "0")));
    }

    [Fact]
    public async Task Enabled_without_a_turn_runner_fails_with_a_clear_error_when_the_hosted_loop_is_resolved()
    {
        var services = Compose(("Enabled", "true"), ("Route", "claude"));
        services.AddSingleton<ILoopGuard>(new FakeLoopGuard());
        await using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains("no ITurnRunner is registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Bazaar:AgentTurns:Scheduler:Enabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_without_a_loop_guard_fails_with_a_clear_error()
    {
        var services = Compose(("Enabled", "true"), ("Route", "claude"));
        services.AddSingleton<ITurnRunner>(new FakeTurnRunner());
        await using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IAgentScheduler>());

        Assert.Contains("no ILoopGuard is registered", ex.Message, StringComparison.Ordinal);
    }

    private sealed class OtherScheduler : IAgentScheduler
    {
        public Task<AgentSchedulerPass> RunOnceAsync(CancellationToken ct) => Task.FromResult(AgentSchedulerPass.Empty);
    }
}
