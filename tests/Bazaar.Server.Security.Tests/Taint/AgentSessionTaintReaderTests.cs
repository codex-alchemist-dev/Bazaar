using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Tests.Taint;

public class AgentSessionTaintReaderTests
{
    private static readonly Guid _s1 = Guid.Parse("0196a6f0-0000-7000-8000-000000000a01");
    private static readonly Guid _s2 = Guid.Parse("0196a6f0-0000-7000-8000-000000000a02");
    private static readonly Guid _s3 = Guid.Parse("0196a6f0-0000-7000-8000-000000000a03");

    private readonly InMemoryAgentSessionDirectory _directory = new();
    private readonly FakeSessionTaintReader _sessions = new();

    private AgentSessionTaintReader Reader() => new(_directory, _sessions);

    private static SessionTaint At(TaintState state) => state switch
    {
        TaintState.Clean => SessionTaint.Clean,
        TaintState.Soft => TaintRules.Apply(SessionTaint.Clean, new TaintInput(Guid.NewGuid(), ContentTrust.Agent, TaintState.Soft), TaintPolicy.Default),
        _ => TaintRules.Apply(SessionTaint.Clean, new TaintInput(Guid.NewGuid(), ContentTrust.External), TaintPolicy.Default),
    };

    [Fact]
    public async Task An_agent_with_no_active_session_is_clean()
    {
        Assert.Equal(TaintState.Clean, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_worst_level_across_active_sessions_wins()
    {
        _directory.Begin("agent-a", _s1);
        _directory.Begin("agent-a", _s2);
        _sessions.Set(_s1, At(TaintState.Clean));
        _sessions.Set(_s2, At(TaintState.Soft));

        Assert.Equal(TaintState.Soft, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));

        _directory.Begin("agent-a", _s3);
        _sessions.Set(_s3, At(TaintState.Hard));
        Assert.Equal(TaintState.Hard, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_agents_sessions_do_not_count()
    {
        _directory.Begin("agent-b", _s1);
        _sessions.Set(_s1, At(TaintState.Hard));

        Assert.Equal(TaintState.Clean, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_ended_session_no_longer_counts()
    {
        _directory.Begin("agent-a", _s1);
        _sessions.Set(_s1, At(TaintState.Hard));
        _directory.End("agent-a", _s1);

        Assert.Equal(TaintState.Clean, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_session_the_store_has_not_seen_is_clean()
    {
        _directory.Begin("agent-a", _s1);

        Assert.Equal(TaintState.Clean, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reading_stops_at_the_first_hard_session()
    {
        _directory.Begin("agent-a", _s1);
        _directory.Begin("agent-a", _s2);
        _sessions.Set(_s1, At(TaintState.Hard));
        _sessions.Set(_s2, At(TaintState.Hard));

        Assert.Equal(TaintState.Hard, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
        Assert.Equal(1, _sessions.Loads);
    }

    [Fact]
    public async Task A_failed_session_read_propagates_so_the_caller_fails_closed()
    {
        _directory.Begin("agent-a", _s1);
        _sessions.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_directory_read_propagates()
    {
        var reader = new AgentSessionTaintReader(new FailingDirectory(), _sessions);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_level_counts_as_hard()
    {
        _directory.Begin("agent-a", _s1);
        _sessions.Set(_s1, SessionTaint.Clean with { State = (TaintState)7 });

        Assert.Equal(TaintState.Hard, await Reader().GetActiveTaintAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task A_blank_agent_id_is_rejected(string agentId)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Reader().GetActiveTaintAsync(agentId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Null_dependencies_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentSessionTaintReader(null!, _sessions));
        Assert.Throws<ArgumentNullException>(() => new AgentSessionTaintReader(_directory, null!));
    }

    [Fact]
    public async Task Directory_lists_each_session_once_and_forgets_ended_ones()
    {
        _directory.Begin("agent-a", _s1);
        _directory.Begin("agent-a", _s1);
        _directory.Begin("agent-a", _s2);
        _directory.End("agent-a", _s1);
        _directory.End("agent-a", _s3);
        _directory.End("agent-x", _s1);

        var active = await _directory.ListActiveAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Equal([_s2], active);
    }

    [Fact]
    public void Directory_rejects_an_empty_session_id()
    {
        Assert.Throws<ArgumentException>(() => _directory.Begin("agent-a", Guid.Empty));
    }

    private sealed class FakeSessionTaintReader : ISessionTaintReader
    {
        private readonly Dictionary<Guid, SessionTaint> _taint = [];

        public bool Fail { get; set; }

        public int Loads { get; private set; }

        public void Set(Guid sessionId, SessionTaint taint) => _taint[sessionId] = taint;

        public Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Loads++;
            if (Fail)
            {
                throw new InvalidOperationException("store down");
            }

            return Task.FromResult(_taint.TryGetValue(sessionId, out var taint) ? taint : SessionTaint.Clean);
        }
    }

    private sealed class FailingDirectory : IAgentSessionDirectory
    {
        public ValueTask<IReadOnlyList<Guid>> ListActiveAsync(string agentId, CancellationToken ct = default) =>
            throw new InvalidOperationException("directory down");
    }
}
