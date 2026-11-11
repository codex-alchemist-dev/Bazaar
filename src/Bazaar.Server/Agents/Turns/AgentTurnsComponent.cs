using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Agents.Turns.Start;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// The agent turn loop as a Bazaar Component (docs/design/06-agent-runtime.md, "Turn loop" and "Subagents"). It owns the context
/// assembler, tool dispatcher, turn runner and subagent runner seams in this folder.
/// </summary>
/// <remarks>
/// Registered: the option defaults (<c>TurnRunnerOptions</c>, <c>SubagentOptions</c>, <c>TurnStarterOptions</c>), the wake chain
/// depth default (<c>NoWakeChainDepthSource</c>, which never reads a depth; RyuseiFuda replaces it), the due-inbox reader, and
/// <c>TurnStarter</c>. <c>TurnStarter</c> is a factory, so it resolves <c>ITurnRunner</c> only when someone resolves it. No
/// <c>ITurnRunner</c> is registered here: resolving <c>TurnStarter</c> fails until a runner exists. <c>TurnRunner</c> and
/// <c>SubagentRunner</c> are not registered: their seams (<c>ITurnContextAssembler</c>, <c>IToolDispatcher</c>,
/// <c>IAgentPostSink</c>) have no production implementations yet.
/// <para>
/// The wake loop is off by default. <c>AgentSchedulerOptions</c> is always bound from <c>Bazaar:AgentTurns:Scheduler</c> and
/// validated here, so a bad value fails at composition. Only when <c>Enabled</c> is true are the due-agent reader, the
/// <c>IAgentScheduler</c> (<c>InboxAgentScheduler</c> unless another one is registered first) and the hosted
/// <c>AgentSchedulerService</c> registered. The hosted service resolves the scheduler when the host starts, so an enabled
/// wake loop with no <c>ITurnRunner</c> or no <c>ILoopGuard</c> stops the host at start with an error that names what is
/// missing.
/// </para>
/// </remarks>
public sealed class AgentTurnsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "agent-turns",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:agent-turns"]);

    public ComponentManifest Manifest => ManifestDefinition;

    // TODO(component): the agent turn tables stay in common/0010_agent_runtime.sql until the migration namespaces move. Do not
    // add a second migration for this component here.
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(new TurnRunnerOptions());
        services.TryAddSingleton(new SubagentOptions());
        services.TryAddSingleton(new TurnStarterOptions());
        services.TryAddSingleton<IWakeChainDepthSource, NoWakeChainDepthSource>();
        services.TryAddSingleton<IDueInboxReader>(sp => new InboxDueReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new TurnStarter(
            sp.GetRequiredService<IDueInboxReader>(),
            sp.GetRequiredService<IWakeChainDepthSource>(),
            sp.GetRequiredService<ITurnRunner>(),
            sp.GetRequiredService<TurnStarterOptions>()));
        services.TryAddSingleton<AgentRepository>();

        AddScheduler(services, configuration);
    }

    /// <summary>Binds the wake loop options and, only when the loop is enabled, registers the scheduler and its hosted service.</summary>
    private static void AddScheduler(IServiceCollection services, IConfiguration configuration)
    {
        var options = new AgentSchedulerOptions();
        configuration.GetSection(AgentSchedulerOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);

        if (!options.Enabled)
            return;

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDueAgentReader>(sp => new InboxDueAgentReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAgentScheduler>(sp =>
        {
            if (sp.GetService<ITurnRunner>() is null)
                throw new InvalidOperationException(
                    $"{AgentSchedulerOptions.SectionName}:Enabled is true, but no ITurnRunner is registered. " +
                    "The wake loop cannot start turns without a turn runner. Register one or switch the wake loop off.");
            var guard = sp.GetService<ILoopGuard>() ?? throw new InvalidOperationException(
                $"{AgentSchedulerOptions.SectionName}:Enabled is true, but no ILoopGuard is registered. " +
                "The wake loop does not wake agents without a loop guard.");

            return new InboxAgentScheduler(
                sp.GetRequiredService<IDueAgentReader>(),
                sp.GetRequiredService<TurnStarter>(),
                guard,
                sp.GetRequiredService<AgentSchedulerOptions>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<InboxAgentScheduler>>());
        });
        services.AddHostedService<AgentSchedulerService>();
    }
}
