using Bazaar.Server.Agents.Data;
using Bazaar.Server.Health;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup.FirstRun;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Interrupts.Tests;

public sealed class InterruptGcResolverTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-00000000000a");
    private static readonly Guid _presenceGc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _generalGc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000c1");
    private static readonly BuildBroken _broken = new("p", "r", default);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakePresence(Guid? gcId) : IPresenceStore
    {
        public ValueTask<bool> UpsertAsync(PresenceRow row, CancellationToken ct) => ValueTask.FromResult(true);

        public ValueTask<PresenceRow?> GetAsync(Guid agentId, CancellationToken ct) =>
            ValueTask.FromResult<PresenceRow?>(new PresenceRow(agentId, 1, default, default, "", default, gcId, null, null, default, default));

        public ValueTask<IReadOnlyList<PresenceRow>> GetAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PresenceRow>>([]);
    }

    private sealed class FakeGeneral(Guid? id) : IGeneralGcReader
    {
        public Task<Guid?> GetGeneralGcIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id);
    }

    private sealed class CapturingLogger : ILogger<InterruptSystemProducer>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Targets : IInterruptSystemTargetsReader
    {
        public Task<InterruptSystemTargets> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(InterruptSystemTargets.AllAgents);
    }

    private sealed class Agents : IAgentDirectoryReader
    {
        public Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentListing>>([new AgentListing(_agent, "a")]);
    }

    private sealed class RecordingStack : IInterruptStackService
    {
        public List<Guid> GcIds { get; } = [];

        public Task<InterruptFrame> PushAsync(Guid agentId, Guid authorId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt = null, bool emergency = false, CancellationToken ct = default)
        {
            GcIds.Add(gcId);
            return Task.FromResult(new InterruptFrame(Guid.NewGuid(), agentId, null, source, reason, gcId, InterruptFrameState.Active, default, null, 1));
        }

        public Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct = default) => Task.FromResult<InterruptFrame?>(null);

        public Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InterruptFrame>>([]);
    }

    [Fact]
    public async Task An_agent_with_a_presence_gc_uses_it()
    {
        var resolver = new InterruptGcResolver(new FakePresence(_presenceGc), new FakeGeneral(_generalGc));

        Assert.Equal(_presenceGc, await resolver.ResolveAsync(_agent, Ct));
    }

    [Fact]
    public async Task An_agent_without_a_presence_gc_falls_back_to_the_general_gc()
    {
        var resolver = new InterruptGcResolver(new FakePresence(null), new FakeGeneral(_generalGc));

        Assert.Equal(_generalGc, await resolver.ResolveAsync(_agent, Ct));
    }

    [Fact]
    public async Task Without_a_presence_store_the_general_gc_is_used()
    {
        var resolver = new InterruptGcResolver(null, new FakeGeneral(_generalGc));

        Assert.Equal(_generalGc, await resolver.ResolveAsync(_agent, Ct));
    }

    [Fact]
    public async Task No_presence_gc_and_no_general_gc_resolves_nothing()
    {
        Assert.Null(await new InterruptGcResolver(new FakePresence(null), new FakeGeneral(null)).ResolveAsync(_agent, Ct));
        Assert.Null(await new InterruptGcResolver(new FakePresence(null), null).ResolveAsync(_agent, Ct));
        Assert.Null(await new InterruptGcResolver().ResolveAsync(_agent, Ct));
    }

    [Fact]
    public async Task The_producer_skips_and_logs_an_agent_with_no_gc_at_all()
    {
        var stack = new RecordingStack();
        var logger = new CapturingLogger();
        var producer = new InterruptSystemProducer(
            stack, new Targets(), new Agents(), new InterruptGcResolver(new FakePresence(null), new FakeGeneral(null)), logger);

        await producer.OnBuildBrokenAsync(_broken, Ct);

        Assert.Empty(stack.GcIds);
        Assert.Contains(logger.Messages, m => m.Contains("no general GC", StringComparison.Ordinal) && m.Contains(_agent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_producer_pushes_to_the_general_gc_when_presence_has_none()
    {
        var stack = new RecordingStack();
        var producer = new InterruptSystemProducer(
            stack, new Targets(), new Agents(), new InterruptGcResolver(new FakePresence(null), new FakeGeneral(_generalGc)));

        await producer.OnBuildBrokenAsync(_broken, Ct);

        Assert.Equal(_generalGc, Assert.Single(stack.GcIds));
    }
}
