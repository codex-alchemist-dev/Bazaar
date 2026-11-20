using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.VmDisplay.Devices.Tests;

internal sealed class FakeControl : IDeviceControl
{
    public List<string> Sent { get; } = [];

    public Task InstallAppAsync(Guid vmId, string packagePathInWorkspace, CancellationToken ct) => Task.CompletedTask;

    public Task LaunchAppAsync(Guid vmId, string appId, CancellationToken ct) => Task.CompletedTask;

    public Task<DeviceScreen> CaptureAsync(Guid vmId, CancellationToken ct) => Task.FromResult(new DeviceScreen([1, 2, 3], DateTimeOffset.UnixEpoch));

    public Task SendInputAsync(Guid vmId, DeviceInput input, CancellationToken ct)
    {
        Sent.Add(input switch
        {
            DeviceInput.Tap t => $"tap {t.X},{t.Y}",
            DeviceInput.Key k => $"key {k.KeyCode}",
            DeviceInput.Text x => $"text {x.Value}",
            _ => "?",
        });
        return Task.CompletedTask;
    }
}

internal sealed class AllowGate : IDisplayGate
{
    public DisplayVerdict CheckCapture(DisplayTarget target) => DisplayVerdict.Allow;

    public DisplayVerdict CheckInput(DisplayTarget target, IReadOnlyList<InputAction> actions) => DisplayVerdict.Allow;
}

internal sealed class NullAudit : IDisplayAuditSink
{
    public List<string> Rows { get; } = [];

    public void Captured(DisplayTarget target, DisplaySourceKind source, int width, int height, int bytes, string sha256) => Rows.Add("captured");

    public void InputSent(DisplayTarget target, DisplaySourceKind source, int actionCount) => Rows.Add($"input:{source}:{actionCount}");

    public void Refused(DisplayTarget target, string operation, string reason) => Rows.Add($"refused:{operation}");
}

public sealed class DeviceDisplayTests
{
    private static readonly DisplayTarget _android = new("agent-1", Guid.CreateVersion7(), DisplayGuestOs.Android, "bz_x");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GuestDisplayService Service, FakeControl Control, NullAudit Audit) Make(bool withDevice = true)
    {
        var control = new FakeControl();
        var audit = new NullAudit();
        IDeviceDisplay[] devices = withDevice ? [new DeviceDisplayAdapter(control)] : [];
        var service = new GuestDisplayService(new VmDisplayOptions(), new AllowGate(), audit, TimeProvider.System, [], [], [], devices);
        return (service, control, audit);
    }

    [Fact]
    public void Android_target_is_served_only_by_the_device_source()
    {
        var (service, _, _) = Make();
        Assert.Equal([DisplaySourceKind.Device], service.AvailableSources(_android));
        Assert.Empty(Make(withDevice: false).Service.AvailableSources(_android));
        Assert.Empty(service.AvailableSources(_android with { Os = DisplayGuestOs.Linux }));
    }

    [Fact]
    public async Task Input_batch_reaches_the_device_and_is_audited()
    {
        var (service, control, audit) = Make();
        await service.SendInputAsync(new InputBatch(_android, [new PointerClick(10, 20), new KeyStroke("Enter"), new TypeText("hi")]), Ct);
        Assert.Equal(["tap 10,20", "key 66", "text hi"], control.Sent);
        Assert.Contains("input:Device:3", audit.Rows);
    }

    [Fact]
    public async Task Input_without_a_device_source_is_refused()
    {
        var (service, _, _) = Make(withDevice: false);
        var e = await Assert.ThrowsAsync<DisplayException>(() => service.SendInputAsync(new InputBatch(_android, [new KeyStroke("Enter")]), Ct));
        Assert.Equal(DisplayFailure.NoSourceAvailable, e.Failure);
    }

    [Fact]
    public async Task A_batch_with_an_inexpressible_action_sends_nothing()
    {
        var (service, control, _) = Make();
        var e = await Assert.ThrowsAsync<DisplayException>(() => service.SendInputAsync(
            new InputBatch(_android, [new KeyStroke("Enter"), new PointerClick(1, 1, PointerButtonKind.Right)]), Ct));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(control.Sent);
    }

    [Theory]
    [InlineData("KeyA", 29)]
    [InlineData("KeyZ", 54)]
    [InlineData("Digit0", 7)]
    [InlineData("F1", 131)]
    [InlineData("F12", 142)]
    [InlineData("ArrowUp", 19)]
    public void Key_codes(string key, int code) =>
        Assert.Equal(new DeviceInput.Key(code), DeviceInputMapper.Map([new KeyStroke(key)])[0]);

    [Fact]
    public void Modifier_chords_pointer_moves_and_scrolls_are_refused()
    {
        Assert.Throws<DisplayException>(() => DeviceInputMapper.Map([new KeyStroke("KeyA", KeyModifiers.Control)]));
        Assert.Throws<DisplayException>(() => DeviceInputMapper.Map([new PointerMove(1, 1)]));
        Assert.Throws<DisplayException>(() => DeviceInputMapper.Map([new PointerScroll(1, 1, 0, 3)]));
    }

    [Fact]
    public void Double_click_is_two_taps() =>
        Assert.Equal(2, DeviceInputMapper.Map([new PointerClick(5, 5, Count: 2)]).Count);

    [Fact]
    public async Task Capture_returns_the_device_png()
    {
        byte[] png = await new DeviceDisplayAdapter(new FakeControl()).CaptureScreenshotPngAsync(_android, Ct);
        Assert.Equal<byte>([1, 2, 3], png);
    }
}
