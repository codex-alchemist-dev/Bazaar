using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns.Context;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Agents.Turns.Start;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Agents.Turns.Tools.Db;
using Bazaar.Server.Components;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Wire.Sync;
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
        services.TryAddSingleton(new TurnStarterOptions());
        services.TryAddSingleton<IWakeChainDepthSource, NoWakeChainDepthSource>();
        services.TryAddSingleton<IDueInboxReader>(sp => new InboxDueReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new TurnStarter(
            sp.GetRequiredService<IDueInboxReader>(),
            sp.GetRequiredService<IWakeChainDepthSource>(),
            sp.GetRequiredService<ITurnRunner>(),
            sp.GetRequiredService<TurnStarterOptions>()));
        services.TryAddSingleton<AgentRepository>();
        services.TryAddSingleton<LeaseRepository>();
        services.TryAddSingleton<InboxRepository>();
        services.TryAddSingleton<TurnRepository>();
        services.TryAddSingleton<TurnStepRepository>();

        // Context (doc 06 "Context assembly"). Sources run in this order: the charter, then the GC backread and trigger.
        // The GC source needs the GC component (IGcStreamSource, IGcReadAccess) and resolves only when a turn is assembled.
        services.TryAddSingleton<ISoulCharterSource, NullSoulCharterSource>();
        services.TryAddSingleton(new GcContextOptions());
        services.AddSingleton<IContextSource>(sp => new SoulContextSource(sp.GetRequiredService<ISoulCharterSource>()));
        services.AddSingleton<IContextSource>(sp => new GcContextSource(
            sp.GetRequiredService<IGcStreamSource>(),
            sp.GetRequiredService<IGcReadAccess>(),
            sp.GetRequiredService<GcContextOptions>()));
        services.TryAddSingleton(ContextBudget.Defaults);
        services.TryAddSingleton<ITurnContextAssembler>(sp => new TurnContextAssembler(
            sp.GetServices<IContextSource>(),
            sp.GetRequiredService<ContextBudget>()));

        // The production turn runner (doc 06 WP5). It is built only when something resolves ITurnRunner. The wake loop (#442)
        // is off by default and is the only thing that resolves it, so no turn starts unless the operator turns it on. The route is the model route every turn
        // uses; the operator sets Bazaar:Turns:Route, and the default is the Haiku 5.5 model id.
        services.TryAddSingleton(sp => new TurnRunner(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IModelGateway>(),
            sp.GetRequiredService<TurnRunnerOptions>()));
        services.TryAddSingleton<ITurnRunner>(sp => new TurnRunAdapter(
            sp.GetRequiredService<TurnRunner>(),
            sp.GetRequiredService<ITurnContextAssembler>(),
            TurnRoute(configuration)));
    }

    /// <summary>The model route every turn uses: <c>Bazaar:Turns:Route</c>, or the Haiku 5.5 model id when unset.</summary>
    internal const string DefaultTurnRoute = "claude-haiku-5-5";

    internal static string TurnRoute(IConfiguration configuration)
    {
        var route = configuration["Bazaar:Turns:Route"];
        return string.IsNullOrWhiteSpace(route) ? DefaultTurnRoute : route.Trim();
    }
}
