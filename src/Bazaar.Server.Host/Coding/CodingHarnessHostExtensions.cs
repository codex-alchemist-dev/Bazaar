using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Workspace;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingReview;
using Bazaar.Server.CodingStore;
using Bazaar.Server.RepoMap;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// folder. Every adapter dependency that has no default (policy engine, subagent runner, workspace, GitHub adapter, token
/// provider, the two directories, the push session directory, the work-share layout, the git identity) must come from the host;
/// a missing one fails at resolution and never opens a gate. Everything here is registered lazily (factories), so a missing
/// dependency can never fail host startup, only the first resolution of the service that needs it. The trace sinks are
/// durable (the <c>tool_calls</c> table) whenever the host has registered a Postgres data source, and in-process otherwise (tests). <see cref="GuestChannelFileAdapter"/> is per guest, so it is built by the session
/// that owns the guest channel and is not registered here.
/// </summary>
public static class CodingHarnessHostExtensions
{
    /// <summary>Registers the six harness modules and the host adapters.</summary>
    public static IServiceCollection AddBazaarCodingHarness(this IServiceCollection services, Action<CodingHarnessHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
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

        services.TryAddSingleton<IGenericToolPolicy>(sp => new ToolPolicyEngineAdapter(
            sp.GetRequiredService<IToolPolicyEngine>(),
            sp.GetRequiredService<CapabilityRegistry>(),
            sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IRuntimeSubagentPort>(sp => new RuntimeSubagentAdapter(
            sp.GetRequiredService<SubagentRunner>(),
            sp.GetRequiredService<IRuntimeSubagentParentDirectory>()));

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

        return services;
    }

    private static string WorkspaceRootOf(IServiceProvider sp) =>
        sp.GetRequiredService<CodingHarnessHostOptions>().WorkspaceRoot
            ?? throw new InvalidOperationException("CodingHarnessHostOptions.WorkspaceRoot must match the host GitWorkspace root.");
}
