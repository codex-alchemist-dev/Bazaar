using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;
using Bazaar.Contracts.Vms;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Devices.Tests;

public class DevicesPaneTests
{
    private static readonly DeviceProviderInfo _android =
        new("android", "android", true, null, "vm", true, true, false, true, true, 4, 1);

    private static readonly DeviceProviderInfo _broken =
        new("microvm", "linux", false, "kvm missing", "vm", true, false, false, false, false, null, 0);

    private static readonly DeviceGuestInfo _guest = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "android", "android", "running");

    [Fact]
    public async Task Refresh_reads_providers_and_guests_from_the_source()
    {
        var pane = new DevicesPaneViewModel(Descriptor(), new FakeSource(new DeviceListReply([_android, _broken], [_guest])));

        await pane.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(pane.IsOffered);
        Assert.Equal(
            new DeviceProviderRow("android (android)", "usable", "vm, headless, save, restore, egress enforced", "1 of 4 running"),
            pane.Providers[0]);
        Assert.Equal(
            new DeviceProviderRow("microvm (linux)", "not usable: kvm missing", "vm, headless, egress not enforced", "0 running"),
            pane.Providers[1]);
        Assert.Equal(new DeviceGuestRow("android (android)", "running", "11111111"), Assert.Single(pane.Guests));
    }

    [Fact]
    public async Task Empty_provider_list_means_the_pane_is_not_offered()
    {
        var pane = new DevicesPaneViewModel(Descriptor(), new FakeSource(new DeviceListReply([], [])));

        await pane.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(pane.IsOffered);
    }

    [Fact]
    public async Task A_refusal_or_a_missing_source_hides_the_pane()
    {
        var denied = new DevicesPaneViewModel(Descriptor(), new ThrowingSource());
        denied.Apply(new DeviceListReply([_android], []));
        Assert.True(denied.IsOffered);

        await denied.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(denied.IsOffered);

        var none = new DevicesPaneViewModel(Descriptor());
        none.Apply(new DeviceListReply([_android], []));

        await none.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(none.IsOffered);
    }

    [Fact]
    public async Task Wire_source_sends_the_devices_list_request()
    {
        var requests = new FakeRequester(new DeviceListReply([_android], []));

        var reply = await new WireDeviceListSource(requests).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WireTypes.DeviceList, requests.Type);
        Assert.Single(reply.Providers);
    }

    [Fact]
    public void Module_registers_its_kind_and_builds_the_pane_with_the_head_source()
    {
        var module = new DevicesPaneModule();
        var context = new PaneModuleContext().With<IDeviceListSource>(new FakeSource(new DeviceListReply([], [])));
        var registry = PaneRegistry.Compose(new StubViewFactory(), [module], context);

        Assert.Equal([DevicesPaneModule.PaneKind], registry.Kinds);
        Assert.IsType<DevicesPaneViewModel>(registry.Create(Descriptor()));
        Assert.Equal(module.Id, Assert.Single(module.Manifest.PaneKinds));
    }

    [AvaloniaFact]
    public void View_shows_providers_reasons_and_guests()
    {
        var pane = new DevicesPaneViewModel(Descriptor());
        pane.Apply(new DeviceListReply([_android, _broken], [_guest]));
        var view = new DevicesPaneView(pane);
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();

        var texts = Texts(view);
        Assert.True(view.IsVisible);
        Assert.Contains("android (android)", texts);
        Assert.Contains("usable", texts);
        Assert.Contains("not usable: kvm missing", texts);
        Assert.Contains("1 of 4 running", texts);
        Assert.Contains("running", texts);
        Assert.Contains("11111111", texts);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public void View_is_hidden_and_empty_when_no_provider_is_enabled()
    {
        var pane = new DevicesPaneViewModel(Descriptor());
        pane.Apply(new DeviceListReply([], []));
        var view = new DevicesPaneView(pane);
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();

        Assert.False(view.IsVisible);
        Assert.Empty(Texts(view));
        window.Close();
    }

    [AvaloniaFact]
    public void View_rebuilds_when_the_list_arrives()
    {
        var pane = new DevicesPaneViewModel(Descriptor());
        var view = new DevicesPaneView(pane);
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();
        Assert.False(view.IsVisible);

        pane.Apply(new DeviceListReply([_android], []));

        Assert.True(view.IsVisible);
        Assert.Contains("No running guests", Texts(view));
        window.Close();
    }

    private static List<string> Texts(Control view) =>
        view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

    private static PaneDescriptor Descriptor() =>
        new(DevicesPaneModule.PaneKind, "Devices", string.Empty, JsonElement.Parse("{}"));

    private sealed class FakeSource(DeviceListReply reply) : IDeviceListSource
    {
        public Task<DeviceListReply> ListAsync(CancellationToken cancellationToken) => Task.FromResult(reply);
    }

    private sealed class ThrowingSource : IDeviceListSource
    {
        public Task<DeviceListReply> ListAsync(CancellationToken cancellationToken) =>
            throw new WireRequestException(new WireError("not_found", null, null));
    }

    private sealed class FakeRequester(object reply) : IWireRequester
    {
        public string? Type { get; private set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Type = type;
            return Task.FromResult((TResponse)reply);
        }
    }

    private sealed class StubViewFactory : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Title };

        public Control WithTitle(string title, Control body) => body;
    }
}
