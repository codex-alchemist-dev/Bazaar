using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.CodingWorkspace.Fakes;

namespace Bazaar.Server.CodingWorkspace.Tests;

internal sealed class Fixture : IDisposable
{
    public static readonly RepoRef Repo = new("repo", "main");

    public Fixture()
    {
        Base = Path.Combine(Path.GetTempPath(), "bazaar-codingws-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Base, "root");
        Directory.CreateDirectory(Root);
        Options = new CodingWorkspaceOptions { Root = Root };
        Source = new ConfiguredAgentWorkShareSource(Options);
        Layout = new WorkShareLayout(Source);
        Engine = new InMemoryCodingGitEngine();
        Locks = new WorktreeLocks();
        Git = new CodingGitService(Layout, Engine, new FakeHostPushPort(), new FakePushGate(), new FakeAgentGitIdentity(), Locks);
        Parents = new InMemoryParentWorkSource();
        Allocator = new GitSubagentWorktreeAllocator(Git, Layout, Parents);
    }

    public string Base { get; }

    public string Root { get; }

    public CodingWorkspaceOptions Options { get; }

    public ConfiguredAgentWorkShareSource Source { get; }

    public WorkShareLayout Layout { get; }

    public InMemoryCodingGitEngine Engine { get; }

    public WorktreeLocks Locks { get; }

    public CodingGitService Git { get; }

    public InMemoryParentWorkSource Parents { get; }

    public GitSubagentWorktreeAllocator Allocator { get; }

    public string ParentPath(string agent = "charles", string repo = "repo", string slug = "fix-login") =>
        Path.Combine(Source.Find(agent)!.WorktreeRoot, repo, slug);

    public void AddParent(string parentId = "p1", string agent = "charles", string slug = "fix-login") =>
        Parents.Set(parentId, new SubagentParentWork(agent, Repo, "gc-1", "task-7", slug, ParentPath(agent, Repo.Id, slug)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
