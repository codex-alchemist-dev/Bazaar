using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.CodingWorkspace;
using Bazaar.Server.CodingWorkspace.Fakes;
using Bazaar.Server.GitHub.Workspace;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Vault.Git;

namespace Bazaar.EndToEnd.Tests.Harness;

/// <summary>
/// The real per-task worktree flow of doc 17 ("Default repo and per-task worktree flow"): <see cref="ICodingTaskWorkspace"/> over
/// <see cref="CodingGitService"/> over a real git engine, with the repository coming from the setting-shaped repo source.
/// Composed in process the way <c>CodingTaskWorkspaceHostTests</c> does, because no turn in the running host opens a task
/// worktree yet (GAPS.md, gap 10). Only the push side is a fake: this run never pushes.
/// </summary>
public sealed class TaskWorkspaceFlow
{
    public const string RepoId = "sample";

    private TaskWorkspaceFlow(CodingTaskWorkspace workspace, CodingGitService service, CodingWorktreeRegistry registry, string root)
    {
        Workspace = workspace;
        Service = service;
        Registry = registry;
        Root = root;
    }

    public ICodingTaskWorkspace Workspace { get; }

    public CodingGitService Service { get; }

    public CodingWorktreeRegistry Registry { get; }

    /// <summary>The work-share root every agent's worktrees live under.</summary>
    public string Root { get; }

    public static TaskWorkspaceFlow Create(string clonePath, string root)
    {
        var options = new CodingWorkspaceOptions { Root = root };
        var layout = new WorkShareLayout(new ConfiguredAgentWorkShareSource(options));
        var repos = new InMemoryCodingRepoSource();
        repos.Set(new CodingRepoRecord(new RepoRef(RepoId, "main"), clonePath, 11, "owner", "Sample", 99));
        var workspace = new GitWorkspace(new LibGit2GitProvider(), new GitWorkspaceOptions { RootPath = root }, new WorkShareLayoutRootResolver(layout));
        var engine = new GitWorkspaceCodingGitEngine(workspace, root, new CodingRepoDirectoryAdapter(repos), layout);
        var service = new CodingGitService(layout, engine, new FakeHostPushPort(), new FakePushGate(), new FakeAgentGitIdentity(), new WorktreeLocks());
        var registry = new CodingWorktreeRegistry();
        return new TaskWorkspaceFlow(new CodingTaskWorkspace(service, repos, layout, new CodingWorktreeRegistryRegistrar(registry), RepoId), service, registry, root);
    }

}
