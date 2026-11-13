using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Tests.Wire;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Testing;

namespace Bazaar.Client.Core.Tests.Panes;

public class GcPaneTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");

    [Fact]
    public void Gc_pane_is_registered_once_in_first_party_registry()
    {
        var registry = FirstPartyPanes.CreateRegistry();

        Assert.True(registry.Contains(GcPaneViewModel.PaneKind));
        Assert.Single(registry.Kinds, kind => kind == GcPaneViewModel.PaneKind);
        Assert.Equal("bazaar.gc", GcPaneViewModel.PaneKind);
    }

    [Fact]
    public void Gc_pane_without_a_chat_has_no_session_and_uses_the_descriptor_title()
    {
        var registry = FirstPartyPanes.CreateRegistry();

        var pane = Assert.IsType<GcPaneViewModel>(registry.Create(PaneRegistryTests.Descriptor(GcPaneViewModel.PaneKind)));

        Assert.Equal("bazaar.gc", pane.Kind);
        Assert.Equal("Title", pane.Title);
        Assert.Null(pane.Chat);
    }

    [Fact]
    public void Gc_pane_falls_back_to_the_default_title_when_the_descriptor_has_none()
    {
        var pane = new GcPaneViewModel(PaneRegistryTests.Descriptor(GcPaneViewModel.PaneKind) with { Title = " " });

        Assert.Equal("GCs", pane.Title);
    }

    [AvaloniaFact]
    public void Gc_pane_is_listed_in_the_phone_pager_from_the_registry()
    {
        var shell = new PhoneShell(FirstPartyPanes.CreateRegistry());

        Assert.Contains(shell.Pages, page => page.Contains(GcPaneViewModel.PaneKind));
    }

    [AvaloniaFact]
    public void Gc_pane_builds_with_its_empty_state_and_with_a_chat()
    {
        var empty = FirstPartyPanes.CreateRegistry().Create(PaneRegistryTests.Descriptor(GcPaneViewModel.PaneKind));
        var emptyWindow = new Window { Content = FirstPartyPanes.CreateRegistry().CreateView(empty), Width = 400, Height = 600 };
        emptyWindow.Show();

        Assert.Contains("No group chat is connected.", emptyWindow.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Empty(emptyWindow.GetVisualDescendants().OfType<ListBox>());

        var transport = new FakeWireTransport();
        var timeline = new ChatTimeline(new Dictionary<Guid, ChatMember>(), "me", TimeZoneInfo.Utc);
        timeline.Apply(new WireGcMessage(_gc, 1, 1, Guid.NewGuid(), Guid.Empty, "hello", DateTimeOffset.UnixEpoch, false, [], null));
        var client = new WireClient(transport, transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "android-test", DeviceClass = "android", ClientBuild = "test" }, new FakeClock());
        var session = new GcChatSession(client, new ChatConnectionSettings(new Uri("ws://h/ws"), "t", _gc, "me"), timeline);
        var chat = new ChatViewModel(session);
        var withChat = FirstPartyPanes.CreateRegistry(chat: chat).Create(PaneRegistryTests.Descriptor(GcPaneViewModel.PaneKind));
        var chatWindow = new Window { Content = FirstPartyPanes.CreateRegistry().CreateView(withChat), Width = 400, Height = 600 };
        chatWindow.Show();

        Assert.Equal(1, chatWindow.GetVisualDescendants().OfType<ListBox>().Single().ItemCount);
        Assert.DoesNotContain("No group chat is connected.", chatWindow.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
    }
}
