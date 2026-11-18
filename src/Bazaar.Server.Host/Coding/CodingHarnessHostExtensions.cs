using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Workspace;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingReview;
using Bazaar.Server.RepoMap;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
/// provider, the two directories, the trace, freeze and push gate ports) must come from the host; a missing one fails at
/// resolution and never opens a gate. <see cref="GuestChannelFileAdapter"/> is per guest, so it is built by the session
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
            sp.GetRequiredService<ICodingRepoDirectory>()));

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
