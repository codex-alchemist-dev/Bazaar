using Bazaar.Contracts.Gateway;
using Bazaar.Core;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Agents.Turns.Context;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Agents.Turns.Start;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Agents.Turns.Tools.Db;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Mentions;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Wire.Sync;
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
/// <c>TurnStarter</c>. Also the repositories, the context sources and assembler, and the production <c>ITurnRunner</c>
/// (<c>SessionRegisteringTurnRunner</c> over <c>TurnRunAdapter</c> over <c>TurnRunner</c>), and the session taint reads for
/// modules (<c>ISessionTaintReader</c> over the session taint store, and <c>AddAgentSessionTaint</c>). <c>TurnStarter</c> and
/// the runner are factories, so they are built only when something resolves them; the runner needs an <c>IModelGateway</c> and
/// the GC component at that point.
/// <para>
/// The wake loop is on by default (overnight plan item 41; <c>Enabled=false</c> turns it off). <c>AgentSchedulerOptions</c> is
/// always bound from <c>Bazaar:AgentTurns:Scheduler</c> and
/// validated here, so a bad value fails at composition. Only when <c>Enabled</c> is true are the due-agent reader, the
/// <c>IAgentScheduler</c> (<c>InboxAgentScheduler</c> unless another one is registered first) and the hosted
/// <c>AgentSchedulerService</c> registered. The hosted service resolves the scheduler when the host starts, so an enabled
/// wake loop with no <c>ITurnRunner</c> or no <c>ILoopGuard</c> stops the host at start with an error that names what is
/// missing, when <c>Enabled</c> was set explicitly. When it was left at its default, a missing runner, gateway or loop guard
/// leaves the loop idle and logs why, so a host that cannot run turns still starts. Every wake is first checked against the kill
/// switch (<c>IAgentWakeGate</c>, backed by the kill switch in <c>Bazaar.Server.AgentSafety</c>; it denies everything when none is registered) and then the loop guard.
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
            sp.GetRequiredService<TimeProvider>(),
            options: null,
            taintListeners: sp.GetServices<ITaintRaisedListener>(),
            logger: sp.GetService<ILogger<ToolDispatcher>>()));
        services.TryAddSingleton<ISessionTaintStore>(sp => new PostgresSessionTaintStore(sp.GetRequiredService<NpgsqlDataSource>()));
        // Modules read session taint through Security.Abstractions (doc 15, "Reads for modules"): the store is the per-session
        // reader, and the directory the runner below registers each run in feeds the per-agent reader.
        services.TryAddSingleton<ISessionTaintReader>(sp => sp.GetRequiredService<ISessionTaintStore>());
        services.AddAgentSessionTaint();
        // Postgres crash markers (turns_inflight/0001). Needs an NpgsqlDataSource from the host.
        services.TryAddSingleton<IInFlightCallStore, PostgresInFlightCallStore>();
        services.TryAddSingleton(new TurnStarterOptions());
        services.TryAddSingleton<IWakeChainDepthSource, NoWakeChainDepthSource>();
        services.TryAddSingleton<IDueInboxReader>(sp => new InboxDueReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new TurnStarter(
            sp.GetRequiredService<IDueInboxReader>(),
            sp.GetRequiredService<IWakeChainDepthSource>(),
            sp.GetRequiredService<ITurnRunner>(),
            sp.GetRequiredService<TurnStarterOptions>(),
            sp.GetServices<ITaskSessionHook>()));
        // Ends task sessions for the Ryusei card transition (ITaskSessionHook). No hook is registered by default, so this is a no-op.
        services.TryAddSingleton(sp => new TaskSessionEnder(sp.GetServices<ITaskSessionHook>()));
        services.TryAddSingleton(sp => new PrincipalHandleRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAgentHandleAssigner>(sp => sp.GetRequiredService<PrincipalHandleRepository>());
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
        // Tool calls the model makes go through the same dispatcher as every other tool call (grants, taint, audit).
        services.TryAddSingleton(sp => new TurnRunner(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IModelGateway>(),
            sp.GetRequiredService<IToolDispatcher>(),
            sp.GetRequiredService<TurnRunnerOptions>(),
            // Optional: with the Thinking GC module loaded, reasoning and steps go to the agent's Thinking GC (doc 01).
            sp.GetService<Bazaar.Server.ThinkingGc.IThinkingRecorder>()));
        // Every run, a subagent child included, is registered in the agent session directory for as long as it runs.
        services.TryAddSingleton<ITurnRunner>(sp => new SessionRegisteringTurnRunner(
            new TurnRunAdapter(
                sp.GetRequiredService<TurnRunner>(),
                sp.GetRequiredService<ITurnContextAssembler>(),
                // Every tool definition a component added. A turn is offered only the ones its allowlist names.
                ToolCatalogue.Build(sp.GetServices<ModelToolDefinition>(), sp.GetRequiredService<IToolRegistry>()),
                // A reply to a wake with no trigger GC (a card assignment) is posted through the GC post sink when the Gc component is present.
                sp.GetService<IAgentPostSink>(),
                sp.GetServices<ITurnReplyTarget>()),
            sp.GetRequiredService<IAgentSessionRegistrar>()));

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
        {
            // Off on purpose: the operator set it, so say so once at start.
            services.AddHostedService(sp => new WakeLoopStateLogger(sp.GetRequiredService<ILogger<WakeLoopStateLogger>>(), enabled: false));
            return;
        }

        var enabledByDefault = configuration.GetSection(AgentSchedulerOptions.SectionName)["Enabled"] is null;
        services.AddHostedService(sp => new WakeLoopStateLogger(sp.GetRequiredService<ILogger<WakeLoopStateLogger>>(), enabled: true, enabledByDefault));
        // Fail closed until a kill-switch-backed gate is registered (Bazaar.Server.AgentSafety registers one over KillSwitchEngine).
        services.TryAddSingleton<IAgentWakeGate, DenyAllWakeGate>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDueAgentReader>(sp => new InboxDueAgentReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAgentToolGrants>(sp => new PostgresAgentToolGrants(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAgentScheduler>(sp =>
        {
            if (!enabledByDefault)
                return BuildInboxScheduler(sp);

            // Default-on path: a host that cannot run turns stays idle and says why; it does not fail to start.
            try
            {
                return BuildInboxScheduler(sp);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                WakeLoopStateLogger.LogIdle(sp.GetRequiredService<ILogger<WakeLoopStateLogger>>(), ex, ex.Message);
                return new IdleAgentScheduler();
            }
        });
        services.AddHostedService<AgentSchedulerService>();
    }

    private static InboxAgentScheduler BuildInboxScheduler(IServiceProvider sp)
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
            sp.GetRequiredService<IAgentToolGrants>(),
            sp.GetRequiredService<AgentSchedulerOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<InboxAgentScheduler>>(),
            sp.GetRequiredService<IAgentWakeGate>());
    }

    /// <summary>The gate used when no kill switch is composed: nothing wakes.</summary>
    private sealed class DenyAllWakeGate : IAgentWakeGate
    {
        public WakeGateDecision Check(Guid agentId) => WakeGateDecision.Deny("No kill switch is registered, so no agent is woken.");
    }

    /// <summary>A scheduler that wakes nothing. Stands in on the default-on path when the host cannot run turns.</summary>
    private sealed class IdleAgentScheduler : IAgentScheduler
    {
        public Task<AgentSchedulerPass> RunOnceAsync(CancellationToken ct) => Task.FromResult(AgentSchedulerPass.Empty);
    }
}
