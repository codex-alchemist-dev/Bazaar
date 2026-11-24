using Bazaar.Server.Agents.Turns.Start;

namespace Bazaar.Server.Tests.Agents.Turns.Start;

/// <summary>Pure tests for <see cref="TaskSessionEnder"/>: fake hooks, no PostgreSQL.</summary>
public sealed class TaskSessionEnderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EndAsync_calls_every_hook_with_the_session_in_registration_order()
    {
        var sessionId = Guid.CreateVersion7();
        var log = new List<(string Name, Guid Session)>();
        var ender = new TaskSessionEnder([new NamedHook("first", log), new NamedHook("second", log)]);

        await ender.EndAsync(sessionId, Ct);

        Assert.Equal([("first", sessionId), ("second", sessionId)], log);
    }

    [Fact]
    public async Task EndAsync_with_no_hooks_completes()
    {
        var ender = new TaskSessionEnder([]);

        await ender.EndAsync(Guid.CreateVersion7(), Ct);
    }

    private sealed class NamedHook(string name, List<(string Name, Guid Session)> log) : ITaskSessionHook
    {
        public Task StartAsync(TaskSessionStart start, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            log.Add((name, sessionId));
            return Task.CompletedTask;
        }
    }
}
