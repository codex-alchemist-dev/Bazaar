using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Archive;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Skimmer;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Skimmer.Digest;
using Bazaar.Server.Skimmer.Scoring;
using Bazaar.Server.Skimmer.Shadow;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Tests.Gc.Skimmer;

/// <summary>The GC side of the Skimmer shadow gate (doc 13): the message source over the log, and the hosted consumers.</summary>
public sealed class GcSkimmerTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly GcPrincipal _owner = new(Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"), PrincipalKind.Owner, "Owner");

    private sealed class Directory(params ShadowAgentEntry[] entries) : IShadowAgentDirectory
    {
        public Task<IReadOnlyList<ShadowAgentEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShadowAgentEntry>>(entries);
    }

    private sealed class MemoryStore : IShadowDecisionStore
    {
        public List<GateDecision> Decisions { get; } = [];

        public Task<long> LastDecidedSeqAsync(Guid gcId, Guid agentId, CancellationToken ct) =>
            Task.FromResult(Decisions.Where(d => d.GcId == gcId && d.AgentId == agentId).Select(d => d.Seq).DefaultIfEmpty(0).Max());

        public Task<bool> RecordAsync(GateDecision decision, string? modelId, CancellationToken ct)
        {
            if (Decisions.Any(d => d.GcId == decision.GcId && d.Seq == decision.Seq && d.AgentId == decision.AgentId))
                return Task.FromResult(false);
            Decisions.Add(decision);
            return Task.FromResult(true);
        }
    }

    private sealed class NoNotifications : IGcNotificationSource
    {
        public async IAsyncEnumerable<GcNotification> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    private static ShadowAgentEntry Agent(Guid id, string handle) =>
        new(new ShadowAgentInfo(id, new AgentProfile(id, handle, [])), new HashSet<string> { handle });

    private GcLogWriter Writer() => new(db.DataSource, new RecordingNotifier());

    private PgGcArchiveBridge Texts() => new(db.DataSource, Writer(), new PlaintextGcBodyCodec());

    private async Task<Guid> NewGcAsync(string type, CancellationToken ct, params GcPrincipal[] members)
    {
        var svc = new GcCreateService(db.DataSource);
        var id = Guid.NewGuid();
        Assert.True(await svc.CreateAsync(id, type, "Room", _owner, null, ct));
        foreach (var m in members)
            Assert.True(await svc.AddMemberAsync(id, m, ct));
        return id;
    }

    private async Task<long> PostAsync(Guid gc, GcPrincipal author, string text, IReadOnlyList<Guid> mentions, CancellationToken ct)
    {
        var body = await new PlaintextGcBodyCodec().SealAsync(author.Id, text, ct);
        var (kind, trust) = author.Kind == PrincipalKind.Owner ? (AuthorKind.Owner, SourceTrust.Owner) : (AuthorKind.Agent, SourceTrust.Agent);
        var result = await Writer().AppendAsync(
            new AppendRequest(gc, GcEventKinds.Message, kind, author.Id, trust, Guid.NewGuid().ToString("D")) { Message = body, Mentions = mentions }, ct);
        return result.Seq;
    }

    [Fact]
    public async Task The_source_reads_messages_in_seq_order_with_text_mentions_owner_flag_and_dm_fact()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Agent, "Charles");
        var gc = await NewGcAsync("dm", ct, agent);
        var first = await PostAsync(gc, _owner, "hello Charles", [agent.Id], ct);
        var second = await PostAsync(gc, agent, "hi", [], ct);
        var entry = Agent(agent.Id, "charles");
        var source = new PgShadowMessageSource(db.DataSource, Texts(), new Directory(entry));

        var all = await source.ReadAfterAsync(gc, entry.Agent, 0, 10, ct);
        Assert.Equal([first, second], all.Select(m => m.Message.Seq));
        Assert.Equal("hello Charles", all[0].Message.Text);
        Assert.Equal("owner", all[0].Message.SenderKind);
        Assert.True(all[0].Facts.IsOwnerAuthor);
        Assert.True(all[0].Facts.IsDmWithAgent);
        Assert.Contains(agent.Id.ToString("D"), all[0].Facts.MentionedHandles);
        Assert.Contains("charles", all[0].Facts.AgentHandles);
        Assert.False(all[1].Facts.IsOwnerAuthor);
        Assert.Empty(all[1].Facts.MentionedHandles);
        Assert.False(all[0].Facts.IsCritical || all[0].Facts.SenderQuarantined);

        var rest = await source.ReadAfterAsync(gc, entry.Agent, first, 10, ct);
        Assert.Equal([second], rest.Select(m => m.Message.Seq));
        Assert.Single(await source.ReadAfterAsync(gc, entry.Agent, 0, 1, ct));
    }

    [Fact]
    public async Task A_general_gc_is_not_a_dm()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Agent, "Charles");
        var gc = await NewGcAsync("general", ct, agent);
        await PostAsync(gc, _owner, "all hands", [], ct);
        var entry = Agent(agent.Id, "charles");

        var messages = await new PgShadowMessageSource(db.DataSource, Texts(), new Directory(entry)).ReadAfterAsync(gc, entry.Agent, 0, 10, ct);

        Assert.False(Assert.Single(messages).Facts.IsDmWithAgent);
    }

    [Fact]
    public async Task A_pass_decides_only_for_member_agents_and_a_second_pass_records_nothing_new()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Agent, "Member");
        var outsider = Guid.NewGuid();
        var gc = await NewGcAsync("general", ct, member);
        await PostAsync(gc, _owner, "please look", [member.Id], ct);
        var directory = new Directory(Agent(member.Id, "member"), Agent(outsider, "outsider"));
        var store = new MemoryStore();
        var consumer = new ShadowConsumer(new ShadowGate(new FakeScorer([])), new PgShadowMessageSource(db.DataSource, Texts(), directory), store);
        var host = new GcShadowConsumerHost(
            new NoNotifications(), directory, consumer, db.DataSource, new GcShadowConsumerOptions(new ShadowSettings()), NullLogger<GcShadowConsumerHost>.Instance);

        Assert.Equal(1, await host.RunGcAsync(gc, ct));
        var decision = Assert.Single(store.Decisions);
        Assert.Equal(member.Id, decision.AgentId);
        Assert.True(decision.Shadow);
        Assert.Equal("wake", decision.Decision);
        Assert.Equal(0, await host.RunGcAsync(gc, ct));
    }
}
