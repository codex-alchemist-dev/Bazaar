using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Workspace;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingReview;
using Bazaar.Server.CodingStore;
using Bazaar.Server.Identity;
using Bazaar.Server.RepoMap;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Subagents;
using Bazaar.Server.Vault.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Bazaar.Server.Host.Coding;

/// <summary>Host settings for the coding harness adapters.</summary>
public sealed class CodingHarnessHostOptions
{
    /// <summary>
    /// The root the host <see cref="GitWorkspace"/> was configured with (<see cref="GitWorkspaceOptions.RootPath"/>).
    /// Required before the git adapters resolve.
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>The GitHub web address used to build pull request URLs.</summary>
    public string GitHubWebBase { get; set; } = "https://github.com";
}

/// <summary>
/// Composes the coding harness (doc 17, "Module layout") onto Server-owned code: the six modules plus the adapters in this
/// folder. The harness is off unless <c>Coding:Harness:Enabled</c> is true; when off it registers nothing. When on, every
/// adapter dependency that has no default must come from the host. Ports with no production source yet are checked at host
/// start (<see cref="CodingHarnessPorts"/>), so a missing one stops the host with the port's name. Adapters are registered
/// with TryAdd and lazily (factories or concrete types), so a host registration made first always wins, and a dependency that is
/// still unbound fails only when that service is first resolved, never opening a gate.
/// The trace sinks are durable (the <c>tool_calls</c> table) whenever the host has registered a Postgres data source, and
/// in-process otherwise (tests). <see cref="GuestChannelFileAdapter"/> is per guest, so it is built by the session
/// that owns the guest channel and is not registered here.
/// </summary>
public static class CodingHarnessHostExtensions
{
    /// <summary>Registers the six harness modules and the host adapters, when <c>Coding:Harness:Enabled</c> is true.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; the harness reads <c>Coding:Harness:Enabled</c> from it. Null means off.</param>
    /// <param name="configure">Host settings, applied only when the harness is on.</param>
    public static IServiceCollection AddBazaarCodingHarness(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<CodingHarnessHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!IsEnabled(configuration))
        {
            return services;
        }

        var options = new CodingHarnessHostOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddBazaarCodingPermissions();
        services.AddBazaarCodingEdit();
        services.AddBazaarRepoMap();
        services.AddBazaarCodingGit();
        services.AddBazaarCodingReview();
        services.AddBazaarSubagents();

        // Durable traces over tool_calls when the host has a Postgres data source (checked at resolution, so registration order does
        // not matter); in-process sinks otherwise, which is for tests only. A host that registers its own trace first keeps it.
        services.AddBazaarCodingStore();
        services.TryAddSingleton<InProcessToolCallTrace>();
        services.TryAddSingleton<IToolCallTrace>(sp => sp.GetService<NpgsqlDataSource>() is null
            ? sp.GetRequiredService<InProcessToolCallTrace>()
            : new ToolCallLogTrace(sp.GetRequiredService<IToolCallLog>()));
        services.TryAddSingleton<InProcessSubagentTrace>();
        services.TryAddSingleton<ISubagentTrace>(sp => sp.GetService<NpgsqlDataSource>() is null
            ? sp.GetRequiredService<InProcessSubagentTrace>()
            : new ToolCallLogSubagentTrace(sp.GetRequiredService<IToolCallLog>()));
        services.TryAddSingleton<IAgentFreeze>(sp => new KillSwitchAgentFreeze(sp.GetRequiredService<KillSwitchEngine>()));
        services.TryAddSingleton<IPushGate>(sp => new PermissionGatePushGate(
            sp.GetRequiredService<IToolPermissionGate>(),
            sp.GetRequiredService<IPushSessionDirectory>()));

        // Server-owned concretes. Each is a no-op when its owner already registered the service (TryAdd); otherwise it resolves
        // through its own constructor, so an unbound dependency of it fails at first resolution.
        services.TryAddSingleton<KillSwitchEngine>();
        services.TryAddSingleton<SubagentRunner>();
        services.TryAddSingleton<IToolPolicyEngine, ToolPolicyEngine>();

        services.TryAddSingleton<IGenericToolPolicy>(sp => new ToolPolicyEngineAdapter(
            sp.GetRequiredService<IToolPolicyEngine>(),
            sp.GetRequiredService<CapabilityRegistry>(),
            sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IRuntimeSubagentPort>(sp => new RuntimeSubagentAdapter(
            sp.GetRequiredService<SubagentRunner>(),
            sp.GetRequiredService<IRuntimeSubagentParentDirectory>()));

        // GitHub side (doc 21). The token provider and the adapter are the concretes in Bazaar.GitHub; their own dependencies
        // (the identity token client, the HTTP client) are the host's to bind.
        services.TryAddSingleton<IGitHubTokenProvider, IdentityServiceGitHubTokenProvider>();
        services.TryAddSingleton<IGitHubRateState, InMemoryGitHubRateState>();
        services.TryAddSingleton<GitHubAdapterOptions>();
        services.TryAddSingleton<IGitHubAdapter, GitHubAdapter>();

        // The workspace reads its root from the harness options, so the two cannot disagree. A work-share layout, when bound, supplies
        // the per-agent root (doc 16).
        services.TryAddSingleton(sp => new GitWorkspaceOptions { RootPath = WorkspaceRootOf(sp) });
        services.TryAddSingleton(sp => new GitWorkspace(
            sp.GetRequiredService<IGitProvider>(),
            sp.GetRequiredService<GitWorkspaceOptions>(),
            sp.GetService<IWorkShareLayout>() is { } layout ? new WorkShareLayoutRootResolver(layout) : null));

        services.TryAddSingleton<IPushSessionDirectory>(sp => new AgentStatePushSessionDirectory(
            sp.GetRequiredService<IAgentStateReader>(),
            sp.GetRequiredService<IPushSessionRecords>()));

        services.TryAddSingleton<ICodingGitEngine>(sp => new GitWorkspaceCodingGitEngine(
            sp.GetRequiredService<GitWorkspace>(),
            WorkspaceRootOf(sp),
            sp.GetRequiredService<ICodingRepoDirectory>(),
            sp.GetService<IWorkShareLayout>()));

        services.TryAddSingleton<IHostPushPort>(sp => new GitWorkspaceHostPushPort(
            sp.GetRequiredService<GitWorkspace>(),
            WorkspaceRootOf(sp),
            sp.GetRequiredService<IGitHubAdapter>(),
            sp.GetRequiredService<IGitHubTokenProvider>(),
            sp.GetRequiredService<ICodingRepoDirectory>(),
            sp.GetRequiredService<CodingHarnessHostOptions>().GitHubWebBase));

        // Fail closed at host start if a port with no production source is unbound.
        services.AddSingleton<IHostedService>(_ => new CodingHarnessStartupCheck(services));

        return services;
    }

    private static bool IsEnabled(IConfiguration? configuration) =>
        bool.TryParse(configuration?["Coding:Harness:Enabled"], out var enabled) && enabled;

    private static string WorkspaceRootOf(IServiceProvider sp) =>
        sp.GetRequiredService<CodingHarnessHostOptions>().WorkspaceRoot
            ?? throw new InvalidOperationException("CodingHarnessHostOptions.WorkspaceRoot must match the host GitWorkspace root.");
}
