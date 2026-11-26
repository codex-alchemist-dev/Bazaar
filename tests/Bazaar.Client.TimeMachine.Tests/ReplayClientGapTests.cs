using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.TimeMachine.Tests;

public class ReplayClientGapTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000aa");

    [Theory]
    [InlineData(null, "{\"text\":\"hi\"}", "hi")]
    [InlineData(GcEventKinds.Message, "\"plain json string\"", "plain json string")]
    [InlineData(GcEventKinds.MessageEdit, "{\"text\":\"edited\"}", "edited")]
    [InlineData(GcEventKinds.Message, "not json at all", "not json at all")]
    [InlineData(GcEventKinds.Message, "{\"kind\":\"image\"}", "[image message]")]
    [InlineData(GcEventKinds.Message, "[1,2]", ReplayMessageText.NoText)]
    [InlineData(GcEventKinds.Message, "{\"text\":5}", ReplayMessageText.NoText)]
    [InlineData(GcEventKinds.System, "{\"text\":\"joined\"}", "[system event]")]
    [InlineData(GcEventKinds.Structured, null, "[structured event]")]
    [InlineData(GcEventKinds.PrEvent, "{}", "[pr event event]")]
    [InlineData(GcEventKinds.Quarantine, null, "[quarantine event]")]
    [InlineData(GcEventKinds.Handover, null, "[handover event]")]
    [InlineData(GcEventKinds.ExternalMail, null, "[external mail event]")]
    [InlineData("hologram", "{\"text\":\"x\"}", "[unsupported message kind: hologram]")]
    public void Decode_follows_the_gc_kinds(string? kind, string? payload, string expected) =>
        Assert.Equal(expected, ReplayMessageText.Decode(kind, payload));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Message_without_a_payload_decodes_to_null(string? payload) =>
        Assert.Null(ReplayMessageText.Decode(GcEventKinds.Message, payload));

    [Fact]
    public void Unknown_kind_never_throws_even_when_odd()
    {
        var odd = new string('k', 200);

        var text = ReplayMessageText.Decode(odd, "{broken");

        Assert.StartsWith("[unsupported message kind: ", text);
        Assert.True(text!.Length < 100);
        Assert.Equal("[unsupported message kind: ]", ReplayMessageText.Decode(string.Empty, null));
    }

    [Fact]
    public void Line_for_a_placeholder_message_reads_normally()
    {
        var view = new ReplayMessageView(Guid.NewGuid(), "Ada", 3, 1, false, false, ReplayMessageText.Decode("hologram", null));

        Assert.Equal("Ada: [unsupported message kind: hologram]", view.Line);
    }

    [Fact]
    public void Viewer_override_is_written_to_the_preference_store()
    {
        var prefs = new InMemoryReplayPreferences();
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, client: null, preferences: prefs);

        vm.SetViewerOverride(ReplayViewMode.History);
        Assert.Equal(ReplayViewMode.History, prefs.GetViewerOverride());

        vm.SetViewerOverride(null);
        Assert.Null(prefs.GetViewerOverride());
    }

    [Fact]
    public void A_stored_override_wins_over_the_install_default_on_a_new_pane()
    {
        var prefs = new InMemoryReplayPreferences();
        prefs.SetViewerOverride(ReplayViewMode.History);

        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, client: null, preferences: prefs);

        Assert.Equal(ReplayViewMode.History, vm.Mode);
        Assert.Equal(ReplayViewMode.Current, vm.InstallDefaultView);
        Assert.Equal(ReplayViewMode.History, vm.ViewerOverride);
    }

    [Fact]
    public void Without_a_store_the_install_default_applies()
    {
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.History, client: null);

        Assert.Null(vm.ViewerOverride);
        Assert.Equal(ReplayViewMode.History, vm.Mode);
    }

    [Fact]
    public void Module_shares_one_store_between_panes_and_prefers_a_handed_over_store()
    {
        var model = JsonElement.Parse($$"""{"gcId":"{{_gc}}","headSeq":4}""");
        var descriptor = new PaneDescriptor(ReplayScrubberViewModel.PaneKind, "Replay", string.Empty, model);

        // No store handed over: the module's in-memory one is shared by panes of the same registry.
        var registry = PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [TimeMachineModule.Pane], new PaneModuleContext());
        var first = Assert.IsType<ReplayScrubberViewModel>(registry.Create(descriptor));
        first.SetViewerOverride(ReplayViewMode.History);
        var second = Assert.IsType<ReplayScrubberViewModel>(registry.Create(descriptor));
        Assert.Equal(ReplayViewMode.History, second.ViewerOverride);

        // A head's own store replaces it.
        var store = new InMemoryReplayPreferences();
        var headRegistry = PaneRegistry.Compose(
            Bazaar.Client.Core.Panes.PaneViews.Instance, [TimeMachineModule.Pane], new PaneModuleContext().With<IReplayPreferences>(store));
        var vm = Assert.IsType<ReplayScrubberViewModel>(headRegistry.Create(descriptor));
        vm.SetViewerOverride(ReplayViewMode.Current);
        Assert.Equal(ReplayViewMode.Current, store.GetViewerOverride());
    }

    [AvaloniaFact]
    public void Scrubber_pane_passes_the_accessible_control_scan()
    {
        var client = new InMemoryReplayClient(10, (gc, seq) => new ReplayFrameView(
            gc, seq, 10, false, [new ReplayMessageView(Guid.NewGuid(), "Ada", seq, 1, false, false, "hello")], [ReplayPartView.Available("Kanban", "1 task")]));
        var registry = PaneRegistry.Compose(
            Bazaar.Client.Core.Panes.PaneViews.Instance, [TimeMachineModule.Pane], new PaneModuleContext().With<IReplayClient>(client));
        var model = JsonElement.Parse($$"""{"gcId":"{{_gc}}","headSeq":10}""");
        var vm = Assert.IsType<ReplayScrubberViewModel>(registry.Create(new PaneDescriptor(ReplayScrubberViewModel.PaneKind, "Replay", string.Empty, model)));
        var window = new Window { Content = registry.CreateView(vm) };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }
}
