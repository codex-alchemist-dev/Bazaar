using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Agents.Turns.Tools.Db;
using Bazaar.Server.Components;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;
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

        // The tool registry is built from every ToolRegistration a component adds. With none registered it is empty, so
        // no tool is offered to a model until its component is enabled and configured (doc 38, Settings).
        services.TryAddSingleton<IToolRegistry>(sp => new ToolRegistry(
            sp.GetServices<ToolRegistration>().Select(registration => (registration.Name, registration.Tool))));

        // Policy for tool calls (doc 15). Fail closed: a tool with no capability is denied, and each component's capabilities
        // are the only ones that exist. Quotas are in memory, so a restart resets them; that is the documented default.
        services.TryAddSingleton<IRateBucketStore, InMemoryRateBucketStore>();
        services.TryAddSingleton(PolicyOptions.Default);
        services.TryAddSingleton(sp => CapabilityRegistry.Create(sp.GetServices<ToolCapability>()));
        services.TryAddSingleton<IToolPolicyEngine>(sp => new ToolPolicyEngine(
            sp.GetRequiredService<CapabilityRegistry>(),
            sp.GetRequiredService<IRateBucketStore>(),
            sp.GetRequiredService<PolicyOptions>()));

        // The dispatcher is the only path a tool call takes: registry, policy, taint, crash markers and audit, in that order.
        services.TryAddSingleton<IToolDispatcher>(sp => new ToolDispatcher(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IToolPolicyEngine>(),
            sp.GetRequiredService<ISessionTaintStore>(),
            sp.GetRequiredService<IInFlightCallStore>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ISessionTaintStore>(sp => new PostgresSessionTaintStore(sp.GetRequiredService<NpgsqlDataSource>()));
        // Postgres crash markers (turns_inflight/0001). Needs an NpgsqlDataSource from the host.
        services.TryAddSingleton<IInFlightCallStore, PostgresInFlightCallStore>();
    }
}
