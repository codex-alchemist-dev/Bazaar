using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Tests.Wire;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Testing;

namespace Bazaar.Client.Core.Tests.Chat;

public class ChatViewModelTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");

    private static (FakeWireTransport Transport, GcChatSession Session, ChatTimeline Timeline) NewSession()
    {
        var transport = new FakeWireTransport();
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember>(), "me", TimeZoneInfo.Utc);
        var client = new WireClient(transport, transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test", Jitter = () => 1.0 },
            new FakeClock());
        var session = new GcChatSession(client, new ChatConnectionSettings(new Uri("ws://h/ws"), "t", _gc, "me"), timeline);
        return (transport, session, timeline);
    }

    /// <summary>Starts the session and waits until the subscribe frame has gone out, so sends reach the fake server.</summary>
    private static async Task<FakeConnection> ConnectAsync(FakeWireTransport transport, GcChatSession session)
    {
        await session.StartAsync(TestContext.Current.CancellationToken);
        while (transport.Count == 0 || !transport.Latest.SentOfType(WireTypes.Subscribe).Any())
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return transport.Latest;
    }

    private static async Task<Envelope> AwaitRequestAsync(FakeConnection connection, string type)
    {
        while (!connection.SentOfType(type).Any())
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return connection.SentOfType(type).Single();
    }

    [Fact]
    public void Refresh_copies_the_timeline_into_lines_and_status()
    {
        var (_, session, timeline) = NewSession();
        timeline.Apply(new WireGcMessage(_gc, 1, 1, Guid.NewGuid(), _author, "hi", DateTimeOffset.UnixEpoch, false, [], null));

        using var vm = new ChatViewModel(session);

        Assert.Single(vm.Lines);
        Assert.Equal("hi", vm.Lines[0].Text);
        Assert.Equal("connecting", vm.Status);
    }

    [Fact]
    public async Task A_successful_send_is_dispatched_back_to_the_ui_thread()
    {
        var (transport, session, _) = NewSession();
        var dispatched = new List<Action>();
        using var vm = new ChatViewModel(session, dispatched.Add);
        var connection = await ConnectAsync(transport, session);

        vm.Draft = "ping";
        var sending = vm.SendDraftAsync(TestContext.Current.CancellationToken);
        var request = await AwaitRequestAsync(connection, WireTypes.GcSend);
        connection.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Response, null, request.Id, null, null,
            DateTimeOffset.UnixEpoch, transport.Codec.EncodePayload(new GcSendResult(3, 1))));
        await sending;

        Assert.NotEmpty(dispatched);
        Assert.Equal(string.Empty, vm.Draft);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Sending_an_empty_draft_does_nothing()
    {
        var (transport, session, _) = NewSession();
        using var vm = new ChatViewModel(session);
        vm.Draft = "   ";

        await vm.SendDraftAsync(TestContext.Current.CancellationToken);

        Assert.Equal("   ", vm.Draft);
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task A_refused_send_keeps_the_draft_and_shows_the_error()
    {
        var (transport, session, _) = NewSession();
        using var vm = new ChatViewModel(session);
        var connection = await ConnectAsync(transport, session);

        vm.Draft = "  hello  ";
        var sending = vm.SendDraftAsync(TestContext.Current.CancellationToken);
        var request = await AwaitRequestAsync(connection, WireTypes.GcSend);
        connection.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Error, null, request.Id, null, null,
            DateTimeOffset.UnixEpoch, transport.Codec.EncodePayload(new WireError("gc.forbidden", "no access", null))));
        await sending;

        Assert.Equal("hello", vm.Draft);
        Assert.Equal("Send failed: gc.forbidden", vm.Error);
    }
}
