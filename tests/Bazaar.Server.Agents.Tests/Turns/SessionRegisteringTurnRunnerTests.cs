using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>
/// The run registers its session for exactly as long as it runs (doc 15, "Reads for modules"), so the per-agent taint reader
/// sees only running sessions, however a run ends.
/// </summary>
public class SessionRegisteringTurnRunnerTests
{
    private static readonly Guid _agentId = Guid.Parse("0196a6f0-0000-7000-8000-000000000c01");
    private static readonly Guid _sessionId = Guid.Parse("0196a6f0-0000-7000-8000-000000000c02");
    private static readonly string _agentKey = _agentId.ToString("D");

    private readonly InMemoryAgentSessionDirectory _directory = new();

    private static TurnRunRequest Request(Guid? sessionId = null, Guid? parentTurnId = null) =>
        new(_agentId, sessionId ?? _sessionId, TurnRole.Actor, "mention", [], "holder", string.Empty, [], ParentTurnId: parentTurnId);

    private Task<IReadOnlyList<Guid>> ActiveAsync() =>
        _directory.ListActiveAsync(_agentKey, TestContext.Current.CancellationToken).AsTask();

    [Fact]
    public async Task The_session_is_active_while_the_run_runs_and_gone_after_it_returns()
    {
        IReadOnlyList<Guid>? during = null;
        var inner = new DelegateTurnRunner(async (_, _) =>
        {
            during = await ActiveAsync();
            return new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, "done", 1);
        });

        var outcome = await new SessionRegisteringTurnRunner(inner, _directory).RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal([_sessionId], during);
        Assert.Empty(await ActiveAsync());
    }

    [Fact]
    public async Task The_session_ends_when_the_run_throws()
    {
        var inner = new DelegateTurnRunner((_, _) => throw new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SessionRegisteringTurnRunner(inner, _directory).RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("boom", ex.Message);
        Assert.Empty(await ActiveAsync());
    }

    [Fact]
    public async Task The_session_ends_when_the_run_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new DelegateTurnRunner(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, null, 0);
        });

        var run = new SessionRegisteringTurnRunner(inner, _directory).RunAsync(Request(), cts.Token);
        await started.Task;
        Assert.Equal([_sessionId], await ActiveAsync());

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(await ActiveAsync());
    }

    [Fact]
    public async Task A_child_in_the_parent_session_does_not_end_the_parent_session()
    {
        IReadOnlyList<Guid>? afterChild = null;
        SessionRegisteringTurnRunner? runner = null;
        var inner = new DelegateTurnRunner(async (request, ct) =>
        {
            if (request.ParentTurnId is null)
            {
                await runner!.RunAsync(Request(parentTurnId: Guid.NewGuid()), ct);
                afterChild = await ActiveAsync();
            }

            return new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, null, 1);
        });
        runner = new SessionRegisteringTurnRunner(inner, _directory);

        await runner.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal([_sessionId], afterChild);
        Assert.Empty(await ActiveAsync());
    }

    [Fact]
    public async Task A_run_with_an_empty_session_id_is_refused_before_it_starts()
    {
        var inner = new DelegateTurnRunner((_, _) => Task.FromResult(new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, null, 1)));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new SessionRegisteringTurnRunner(inner, _directory).RunAsync(Request(Guid.Empty), TestContext.Current.CancellationToken));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task The_end_uses_the_same_agent_and_session_as_the_begin()
    {
        var registrar = new RecordingRegistrar();
        var inner = new DelegateTurnRunner((_, _) => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SessionRegisteringTurnRunner(inner, registrar).RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal([("begin", _agentKey, _sessionId), ("end", _agentKey, _sessionId)], registrar.Calls);
    }

    [Fact]
    public void The_constructor_rejects_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionRegisteringTurnRunner(null!, _directory));
        Assert.Throws<ArgumentNullException>(() => new SessionRegisteringTurnRunner(new DelegateTurnRunner((_, _) => throw new InvalidOperationException()), null!));
    }

    private sealed class DelegateTurnRunner(Func<TurnRunRequest, CancellationToken, Task<TurnRunOutcome>> run) : ITurnRunner
    {
        public int Calls { get; private set; }

        public Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return run(request, cancellationToken);
        }
    }

    private sealed class RecordingRegistrar : IAgentSessionRegistrar
    {
        public List<(string Kind, string AgentId, Guid SessionId)> Calls { get; } = [];

        public void BeginSession(string agentId, Guid sessionId) => Calls.Add(("begin", agentId, sessionId));

        public void EndSession(string agentId, Guid sessionId) => Calls.Add(("end", agentId, sessionId));
    }
}
