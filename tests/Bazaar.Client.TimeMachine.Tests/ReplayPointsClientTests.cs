using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.TimeMachine.Tests;

public class ReplayPointsClientTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000aa");

    private static InMemoryReplayClient Client(int count = 5)
    {
        var client = new InMemoryReplayClient(10);
        for (var i = 1; i <= count; i++)
        {
            client.Points.Add(new ReplayPointView(i * 2, DateTimeOffset.UnixEpoch.AddMinutes(i), i == 3 ? "audit" : "message", i == 3 ? "config.changed" : "Message"));
        }

        return client;
    }

    private sealed class FakeRequester : IWireRequester
    {
        public List<(string Type, object Payload)> Sent { get; } = [];

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload!));
            object answer = new ReplayPointsResult(
                _gc, [new ReplayPointWire(4, DateTimeOffset.UnixEpoch, "audit", "config.changed")], 4, 10);
            return Task.FromResult((TResponse)answer);
        }
    }

    [Fact]
    public async Task Wire_client_sends_replay_points_and_maps_the_page()
    {
        var requester = new FakeRequester();

        var page = await new WireReplayClient(requester).GetPointsAsync(_gc, 2, 50, TestContext.Current.CancellationToken);

        var sent = Assert.Single(requester.Sent);
        Assert.Equal(WireTypes.ReplayPoints, sent.Type);
        Assert.Equal(new ReplayPointsRequest(_gc, 2, 50), sent.Payload);
        var point = Assert.Single(page.Points);
        Assert.True(point.IsAudit);
        Assert.Equal(4, page.Next);
    }

    [Fact]
    public async Task Pane_loads_pages_and_jumps_to_a_point()
    {
        var client = Client();
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, client, (_, _) => Task.CompletedTask);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.LoadPointsAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(3, vm.Points.Count);
        Assert.True(vm.CanLoadMorePoints);

        await vm.LoadPointsAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(5, vm.Points.Count);
        Assert.False(vm.CanLoadMorePoints);

        await vm.JumpToAsync(vm.Points[1], TestContext.Current.CancellationToken);
        Assert.Equal(4, vm.CursorSeq);
        Assert.True(vm.IsReplaying);
        Assert.Empty(client.ForkRequests);
    }

    [Fact]
    public async Task A_failure_shows_text_and_keeps_the_list()
    {
        var client = Client();
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, client, (_, _) => Task.CompletedTask);
        await vm.LoadPointsAsync(2, TestContext.Current.CancellationToken);
        client.Failure = new ReplayClientException("nope");

        await vm.LoadPointsAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal("nope", vm.ErrorText);
        Assert.Equal(2, vm.Points.Count);
    }

    [Fact]
    public void A_client_without_a_list_has_no_points()
    {
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, null);

        Assert.False(vm.HasPoints);
        Assert.False(vm.CanLoadMorePoints);
    }

    [AvaloniaFact]
    public void View_lists_points_and_a_click_moves_the_cursor()
    {
        var client = Client();
        var registry = PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [TimeMachineModule.Pane], new PaneModuleContext().With<IReplayClient>(client));
        var model = JsonElement.Parse($$"""{"gcId":"{{_gc}}","headSeq":10}""");
        var vm = Assert.IsType<ReplayScrubberViewModel>(registry.Create(new PaneDescriptor(ReplayScrubberViewModel.PaneKind, "Replay", string.Empty, model)));
        var view = registry.CreateView(vm);
        new Window { Content = view }.Show();

        var list = view.GetLogicalDescendants().OfType<StackPanel>().Single(c => c.Name == ReplayScrubberView.PointsName);
        var buttons = list.Children.OfType<Button>().ToList();
        Assert.Equal(5, buttons.Count);
        Assert.Contains("config.changed", (string)buttons[2].Content!);

        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(4, vm.CursorSeq);
    }
}
