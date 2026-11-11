using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Agents.Turns.Start;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// <c>ITurnRunner</c> is registered here, and no background service starts turns: resolving <c>TurnStarter</c> fails until a
/// runner exists. <c>TurnRunner</c> and <c>SubagentRunner</c> are not registered: their seams (<c>ITurnContextAssembler</c>,
/// <c>IToolDispatcher</c>, <c>IAgentPostSink</c>) have no production implementations yet.
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
    }
}
