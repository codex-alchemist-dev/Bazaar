using Bazaar.Server.Agents.Turns.Start;
using Bazaar.Server.CodingWorkspace;
using Bazaar.Server.Host.Coding;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingTaskSessionHookTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartOpensOnceRecordsTheKeyAndEndCloses()
    {
        var ws = new FakeWorkspace();
        var keys = new CodingSessionWorktreeKeys();
        var hook = new CodingTaskSessionHook(ws, keys);
        var start = new TaskSessionStart(Guid.NewGuid(), Guid.NewGuid(), null, "fix-bug", null, null);

        await hook.StartAsync(start, _ct);
        await hook.StartAsync(start, _ct);
        Assert.Equal(1, ws.Opened);
        Assert.Equal("fix-bug", ws.LastTask);
        Assert.Equal("key1", keys.KeyFor(start.SessionId));

        await hook.EndAsync(start.SessionId, _ct);
        await hook.EndAsync(start.SessionId, _ct);
        Assert.Equal(["key1"], ws.Closed);
        Assert.Null(keys.KeyFor(start.SessionId));
    }

    [Fact]
    public async Task NoTaskOpensNothing()
    {
        var ws = new FakeWorkspace();
        await new CodingTaskSessionHook(ws, new CodingSessionWorktreeKeys())
            .StartAsync(new TaskSessionStart(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null), _ct);
        Assert.Equal(0, ws.Opened);
    }

    [Fact]
    public async Task TaskIdFallsBackToLowercaseSlug()
    {
        var ws = new FakeWorkspace();
        var id = Guid.NewGuid();
        await new CodingTaskSessionHook(ws, new CodingSessionWorktreeKeys())
            .StartAsync(new TaskSessionStart(Guid.NewGuid(), Guid.NewGuid(), null, null, null, id), _ct);
        Assert.Equal(id.ToString("N"), ws.LastTask);
    }

    private sealed class FakeWorkspace : ICodingTaskWorkspace
    {
        public int Opened;
        public string? LastTask;
        public List<string> Closed { get; } = [];

        public Task<CodingTaskWorktree> OpenAsync(string agentId, string taskId, string? repoId = null, string? gcId = null, CancellationToken cancellationToken = default)
        {
            Opened++;
            LastTask = taskId;
            return Task.FromResult(new CodingTaskWorktree("key" + Opened, agentId, "r", taskId, "agent/" + taskId, "/x"));
        }

        public Task CloseAsync(string worktreeKey, CancellationToken cancellationToken = default)
        {
            Closed.Add(worktreeKey);
            return Task.CompletedTask;
        }

        public Task DiscardAsync(string worktreeKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
