using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Context;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Tests.Agents.Turns.Context;

public class GcContextSourceTests
{
    private static readonly Guid _gc = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _agent = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Denied_grant_gives_only_the_fixed_notice_so_the_trigger_thread_is_still_found()
    {
        var source = Source(new FakeStream([]), new FakeAccess(GcReadGrant.Denied));

        var items = await source.ReadAsync(Request(triggerSeq: 3), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(ContextSection.GcBackread, item.Section);
        Assert.Equal(_gc.ToString("D"), item.ItemId);
        Assert.DoesNotContain(items, i => i.Section == ContextSection.Inbox);
    }

    [Fact]
    public async Task Full_grant_gives_backread_and_the_trigger_message_verbatim()
    {
        var stream = new FakeStream([Msg(1, "first"), Msg(2, "second"), Msg(3, "trigger text")]);
        var source = Source(stream, new FakeAccess(GcReadGrant.Full));

        var items = await source.ReadAsync(Request(triggerSeq: 3), CancellationToken.None);

        var backread = Assert.Single(items, i => i.Section == ContextSection.GcBackread);
        Assert.Equal("3", backread.Version);
        Assert.Contains("[1] ", backread.Text);
        var inbox = Assert.Single(items, i => i.Section == ContextSection.Inbox);
        Assert.StartsWith("[3] ", inbox.Text, StringComparison.Ordinal);
        Assert.EndsWith(": trigger text", inbox.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_after_left_seq_are_not_shown()
    {
        var stream = new FakeStream([Msg(1, "kept"), Msg(2, "kept too"), Msg(3, "after leaving")]);
        var source = Source(stream, new FakeAccess(GcReadGrant.Through(2)));

        var items = await source.ReadAsync(Request(triggerSeq: null), CancellationToken.None);

        var backread = Assert.Single(items);
        Assert.Contains("kept too", backread.Text);
        Assert.DoesNotContain("after leaving", backread.Text);
        Assert.Equal("2", backread.Version);
    }

    [Fact]
    public async Task A_trigger_older_than_the_tail_is_fetched_directly()
    {
        var messages = Enumerable.Range(1, 30).Select(n => Msg(n, $"message {n}")).ToList();
        var source = Source(new FakeStream(messages), new FakeAccess(GcReadGrant.Full), new GcContextOptions { BackreadMessages = 5 });

        var items = await source.ReadAsync(Request(triggerSeq: 2), CancellationToken.None);

        var inbox = Assert.Single(items, i => i.Section == ContextSection.Inbox);
        Assert.Contains("message 2", inbox.Text);
    }

    [Fact]
    public async Task Long_message_text_is_cut_to_the_per_message_cap()
    {
        var stream = new FakeStream([Msg(1, new string('x', 5000))]);
        var source = Source(stream, new FakeAccess(GcReadGrant.Full), new GcContextOptions { MaxMessageChars = 100 });

        var items = await source.ReadAsync(Request(triggerSeq: null), CancellationToken.None);

        Assert.True(Assert.Single(items).Text.Length < 200);
    }

    [Fact]
    public async Task No_trigger_gc_means_no_context_items()
    {
        var source = Source(new FakeStream([Msg(1, "x")]), new FakeAccess(GcReadGrant.Full));

        var items = await source.ReadAsync(
            new TurnContextRequest(_agent, Guid.NewGuid(), TurnRole.Actor, "wake", [], TriggerGcId: null), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Charter_source_returns_nothing_by_default()
    {
        var items = await new SoulContextSource(new NullSoulCharterSource()).ReadAsync(Request(null), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Charter_source_puts_text_in_the_system_section()
    {
        var charters = new FixedCharter("You are the reviewer.");

        var items = await new SoulContextSource(charters).ReadAsync(Request(null), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(ContextSection.System, item.Section);
        Assert.Equal("You are the reviewer.", item.Text);
    }

    private static TurnContextRequest Request(long? triggerSeq) =>
        new(_agent, Guid.NewGuid(), TurnRole.Actor, "wake", [], TriggerGcId: _gc.ToString("D"), TriggerGcSeq: triggerSeq);

    private static GcContextSource Source(IGcStreamSource stream, IGcReadAccess access, GcContextOptions? options = null) =>
        new(stream, access, options ?? new GcContextOptions());

    private static WireGcMessage Msg(long n, string text) => new(
        _gc, n, n, Guid.NewGuid(), Guid.NewGuid(), text, DateTimeOffset.UnixEpoch, false, [], null);

    private sealed class FakeAccess(GcReadGrant grant) : IGcReadAccess
    {
        public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct)
        {
            Assert.Equal(PrincipalKind.Agent, viewer.Kind);
            return ValueTask.FromResult(grant);
        }
    }

    private sealed class FakeStream(IReadOnlyList<WireGcMessage> messages) : IGcStreamSource
    {
        public IReadOnlyList<WireGcMessage> Messages { get; } = messages;

        public ValueTask<IReadOnlyList<WireGcMessage>> ReadTailAsync(Guid gcId, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WireGcMessage>>(Messages.TakeLast(limit).ToList());

        public ValueTask<IReadOnlyList<WireGcMessage>> ReadAsync(Guid gcId, long after, long editAfter, long afterEv, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WireGcMessage>>(Messages.Where(m => m.N > after).Take(limit).ToList());

        public ValueTask<GcHead?> GetHeadAsync(Guid gcId, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<int> CountUnreadAsync(Guid gcId, Guid viewerId, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> CanReadAsync(Guid principalId, Guid gcId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FixedCharter(string text) : ISoulCharterSource
    {
        public ValueTask<string?> ReadCharterAsync(Guid agentId, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(text);
    }
}
