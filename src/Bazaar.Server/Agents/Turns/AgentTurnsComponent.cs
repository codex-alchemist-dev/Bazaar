using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Agents.Turns.Tools.Db;
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
/// Only the option defaults are registered today. The seams (<c>ITurnContextAssembler</c>, <c>IToolDispatcher</c>,
/// <c>IAgentPostSink</c>) have no production implementations yet, so <c>TurnRunner</c> and <c>SubagentRunner</c> are not
/// registered: resolving them would fail at runtime. Register them here once those implementations exist.
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
        services.TryAddSingleton<ISessionTaintStore>(sp => new PostgresSessionTaintStore(sp.GetRequiredService<NpgsqlDataSource>()));
        // Postgres crash markers (turns_inflight/0001). Needs an NpgsqlDataSource from the host.
        services.TryAddSingleton<IInFlightCallStore, PostgresInFlightCallStore>();
    }
}
