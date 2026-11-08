using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Tests.Wire;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Testing;

namespace Bazaar.Client.Core.Tests.Chat;

public class GcChatSessionTests : IAsyncLifetime
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly string _stream = WireStreams.Gc(_gc);

    private readonly FakeWireTransport _transport = new();
    private readonly ChatConnectionSettings _settings = new(new Uri("ws://localhost/ws"), "token", _gc, "me");
    private readonly ChatTimeline _timeline = new(new Dictionary<Guid, ChatMember>(), "me", TimeZoneInfo.Utc);
    private readonly GcChatSession _session;

    public GcChatSessionTests()
    {
        var client = new WireClient(_transport, _transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test", Jitter = () => 1.0 },
            new FakeClock());
        _session = new GcChatSession(client, _settings, _timeline);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static WireGcMessage Msg(long n, long ev, string text) =>
        new(_gc, n, ev, Guid.NewGuid(), _author, text, DateTimeOffset.UnixEpoch, ev > 1, [], null);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Start_subscribes_and_incoming_messages_reach_the_timeline()
    {
        var changes = 0;
        _session.Changed += (_, _) => Interlocked.Increment(ref changes);

        await _session.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        var conn = _transport.Latest;
        conn.PushEvent(WireTypes.GcMessage, Msg(1, 1, "hello"), _stream, 1);
        conn.PushEvent(WireTypes.GcMessage, Msg(2, 1, "again"), _stream, 2);
        await Until(() => _timeline.Lines().Count == 2);

        Assert.Equal(["hello", "again"], _timeline.Lines().Select(l => l.Text));
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Messages_of_other_group_chats_are_ignored()
    {
        await _session.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        var other = new WireGcMessage(Guid.NewGuid(), 1, 1, Guid.NewGuid(), _author, "elsewhere", DateTimeOffset.UnixEpoch, false, [], null);
        _transport.Latest.PushEvent(WireTypes.GcMessage, other, _stream, 1);
        _transport.Latest.PushEvent(WireTypes.GcMessage, Msg(2, 1, "here"), _stream, 2);
        await Until(() => _timeline.Lines().Count == 1);

        Assert.Equal("here", _timeline.Lines().Single().Text);
    }

    [Fact]
    public async Task Send_posts_to_the_group_chat_and_marks_the_message_as_own()
    {
        await _session.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        var send = _session.SendAsync("ping", TestContext.Current.CancellationToken);
        await Until(() => _transport.Latest.SentOfType(WireTypes.GcSend).Any());
        var request = _transport.Latest.SentOfType(WireTypes.GcSend).Single();
        Assert.Equal("ping", _transport.Codec.DecodePayload<GcSend>(request).Text);

        _transport.Latest.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Response, null, request.Id, null, null,
            DateTimeOffset.UnixEpoch, _transport.Codec.EncodePayload(new GcSendResult(7, 1))));
        await send;

        _transport.Latest.PushEvent(WireTypes.GcMessage, Msg(7, 1, "ping"), _stream, 7);
        await Until(() => _timeline.Lines().Count == 1);
        Assert.True(_timeline.Lines().Single().Own);
    }

    [Fact]
    public async Task Status_reports_connected_then_closed_after_disposal()
    {
        Assert.Equal("connecting", _session.Status);
        await _session.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => _session.Status == "connected");

        await _session.DisposeAsync();
        Assert.Equal("closed", _session.Status);
    }
}
