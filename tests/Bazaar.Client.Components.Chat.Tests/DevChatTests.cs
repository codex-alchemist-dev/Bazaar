using System.Threading.Channels;
using Bazaar.Client.Components.Chat;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Components.Chat.Tests;

public sealed class DevChatTests
{
    private static readonly Guid _gc = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherGc = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _charles = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _owner = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static WireGcMessage Msg(Guid gc, long n, long ev, string text, Guid author) =>
        new(gc, n, ev, Guid.NewGuid(), author, text, DateTimeOffset.UnixEpoch, Edited: ev > 1, [], null);

    private sealed class FakeWire : IGcWire
    {
        private readonly Channel<WireGcMessage> _messages = Channel.CreateUnbounded<WireGcMessage>();

        public List<GcHistory> HistoryRequests { get; } = [];
        public List<string> Subscriptions { get; } = [];
        public List<GcSend> Sends { get; } = [];
        public GcPage HistoryPage { get; set; } = new([], false);

        public ChannelReader<WireGcMessage> Messages => _messages.Reader;

        public Task SubscribeAsync(string stream, CancellationToken cancellationToken)
        {
            Subscriptions.Add(stream);
            return Task.CompletedTask;
        }

        public Task<GcSendResult> SendAsync(GcSend request, CancellationToken cancellationToken)
        {
            Sends.Add(request);
            return Task.FromResult(new GcSendResult(Sends.Count, 1));
        }

        public Task<GcPage> HistoryAsync(GcHistory request, CancellationToken cancellationToken)
        {
            HistoryRequests.Add(request);
            return Task.FromResult(HistoryPage);
        }

        public void Emit(WireGcMessage message) => _messages.Writer.TryWrite(message);

        public void Complete() => _messages.Writer.TryComplete();
    }

    [Fact]
    public void Transcript_keeps_the_highest_version_per_sequence_number()
    {
        var transcript = new ChatTranscript(_gc);

        transcript.Apply(Msg(_gc, 1, 1, "first draft", _owner));
        var changed = transcript.Apply(Msg(_gc, 1, 2, "first draft, edited", _owner));
        var stale = transcript.Apply(Msg(_gc, 1, 1, "older version", _owner));

        Assert.True(changed);
        Assert.False(stale);
        Assert.Single(transcript.Items);
        Assert.Equal("first draft, edited", transcript.Items[0].Text);
    }

    [Fact]
    public void Transcript_orders_by_sequence_number_and_ignores_other_chats()
    {
        var transcript = new ChatTranscript(_gc);

        transcript.Apply(Msg(_gc, 3, 1, "third", _charles));
        transcript.Apply(Msg(_otherGc, 2, 1, "elsewhere", _charles));
        transcript.Apply(Msg(_gc, 2, 1, "second", _owner));

        Assert.Equal(["second", "third"], transcript.Items.Select(m => m.Text));
    }

    [Fact]
    public async Task Open_loads_newest_history_then_subscribes_to_the_chat_stream()
    {
        var wire = new FakeWire
        {
            HistoryPage = new GcPage([Msg(_gc, 1, 1, "hello", _owner), Msg(_gc, 2, 1, "hi, it is _charles", _charles)], false),
        };
        var session = new DevChatSession(wire, _gc, historyPageSize: 10);

        await session.OpenAsync(CancellationToken.None);

        Assert.Equal(new GcHistory(_gc, long.MaxValue, 10), Assert.Single(wire.HistoryRequests));
        Assert.Equal(["gc:" + _gc.ToString("D")], wire.Subscriptions);
        Assert.Equal(["hello", "hi, it is _charles"], session.Transcript.Items.Select(m => m.Text));
    }

    [Fact]
    public async Task Pump_applies_live_replies_from_agents_to_the_transcript()
    {
        var wire = new FakeWire();
        var session = new DevChatSession(wire, _gc);
        wire.Emit(Msg(_gc, 1, 1, "build is green", _charles));
        wire.Emit(Msg(_otherGc, 2, 1, "not for this chat", _charles));
        wire.Complete();

        await session.PumpAsync(CancellationToken.None);

        Assert.Equal(["build is green"], session.Transcript.Items.Select(m => m.Text));
    }

    [Fact]
    public async Task Send_trims_text_and_targets_the_configured_chat()
    {
        var wire = new FakeWire();
        var session = new DevChatSession(wire, _gc);

        var result = await session.SendAsync("  can you scaffold the chat pane  ", replyToN: 7, CancellationToken.None);

        Assert.Equal(new GcSend(_gc, "can you scaffold the chat pane", null, 7), Assert.Single(wire.Sends));
        Assert.Equal(1, result.N);
    }

    [Fact]
    public async Task Send_rejects_blank_text_before_it_reaches_the_wire()
    {
        var wire = new FakeWire();
        var session = new DevChatSession(wire, _gc);

        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAsync("   ", null, CancellationToken.None));
        Assert.Empty(wire.Sends);
    }

    [Fact]
    public void Constructor_rejects_a_page_size_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevChatSession(new FakeWire(), _gc, historyPageSize: 0));
    }
}
