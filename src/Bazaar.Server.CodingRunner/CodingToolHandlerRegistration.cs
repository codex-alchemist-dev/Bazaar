using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingReview;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.RepoMap;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingRunner;

/// <summary>Registration of the coding tool handlers (doc 17, WP15 follow-up: search, repo map, git, review, subagent spawn).</summary>
public static class CodingToolHandlerRegistration
{
    /// <summary>
    /// Registers one <see cref="ICodingToolHandler"/> per tool: <c>read</c>, <c>list</c>, <c>search</c>, <c>repo_map</c>, <c>git_diff</c>,
    /// <c>git_commit</c>, <c>git_push_draft_pr</c>, <c>review</c> and <c>subagent_spawn</c>, <c>shell</c> and <c>run_checks</c>. The services they wrap, the
    /// <see cref="ICodingToolHostContext"/> and the <see cref="IWorktreeFilesSource"/> come from the host; none is defaulted, so a
    /// missing one fails resolution instead of running a tool unguarded. The edit handler is registered separately by the host.
    /// </summary>
    public static IServiceCollection AddBazaarCodingToolHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICodingToolHandler>(sp => new ReadToolHandler(
            sp.GetRequiredService<IWorktreeFilesSource>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        services.AddSingleton<ICodingToolHandler>(sp => new ListToolHandler(
            sp.GetRequiredService<IWorktreeFilesSource>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        services.AddSingleton<ICodingToolHandler>(sp => new SearchToolHandler(sp.GetRequiredService<ISearchTool>()));
        services.AddSingleton<ICodingToolHandler>(sp => new RepoMapToolHandler(sp.GetRequiredService<IRepoMapBuilder>()));
        services.AddSingleton<ICodingToolHandler>(sp => new GitDiffToolHandler(
            sp.GetRequiredService<CodingGitService>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        services.AddSingleton<ICodingToolHandler>(sp => new GitCommitToolHandler(
            sp.GetRequiredService<CodingGitService>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        services.AddSingleton<ICodingToolHandler>(sp => new GitPushDraftPrToolHandler(
            sp.GetRequiredService<CodingGitService>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        services.AddSingleton<ICodingToolHandler>(sp => new ReviewToolHandler(
            sp.GetRequiredService<ReviewService>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        services.AddSingleton<ICodingToolHandler>(sp => new SubagentSpawnToolHandler(
            sp.GetRequiredService<ISubagentSpawner>(),
            sp.GetRequiredService<ICodingToolHostContext>()));
        return services;
    }

    /// <summary>
    /// Registers the <c>shell</c> and <c>run_checks</c> handlers. They need <see cref="IGuestWorktreeSource"/>, <see cref="ICheckSubjectSource"/> and the
    /// check run store from the host, so a host calls this only once those are bound.
    /// </summary>
    public static IServiceCollection AddBazaarCodingGuestToolHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICodingToolHandler>(sp => new ShellToolHandler(sp.GetRequiredService<IGuestWorktreeSource>()));
        services.AddSingleton<ICodingToolHandler>(sp => new RunChecksToolHandler(sp.GetRequiredService<ICheckSubjectSource>(), sp.GetRequiredService<IGuestWorktreeSource>(), sp.GetRequiredService<Bazaar.Server.CodingStore.ICheckRunStore>()));
        return services;
    }
}
