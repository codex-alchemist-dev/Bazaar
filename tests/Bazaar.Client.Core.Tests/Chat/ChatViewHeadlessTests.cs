using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Tests.Wire;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Testing;

namespace Bazaar.Client.Core.Tests.Chat;

public class ChatViewHeadlessTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    [AvaloniaFact]
    public void Main_window_shows_the_chat_lines_and_the_draft_box()
    {
        var transport = new FakeWireTransport();
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember> { [Guid.Empty] = new("Charles", IsAgent: true) }, "me", TimeZoneInfo.Utc);
        timeline.Apply(new WireGcMessage(_gc, 1, 1, Guid.NewGuid(), Guid.Empty, "first line", DateTimeOffset.UnixEpoch, false, [], null));
        var client = new WireClient(transport, transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test" }, new FakeClock());
        var session = new GcChatSession(client, new ChatConnectionSettings(new Uri("ws://h/ws"), "t", _gc, "me"), timeline);
        var vm = new ChatViewModel(session);

        var window = new MainWindow(vm);
        window.Show();

        var lines = window.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(1, lines.ItemCount);
        Assert.NotNull(window.GetVisualDescendants().OfType<TextBox>().SingleOrDefault());
    }

    [AvaloniaFact]
    public void Main_window_without_a_chat_keeps_the_plain_start_view()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Empty(window.GetVisualDescendants().OfType<ListBox>());
        Assert.Equal("Bazaar", window.Title);
    }
}
