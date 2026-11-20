using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.VmDisplay.Devices;

/// <summary>
/// Adapts a device provider (<see cref="IDeviceControl"/>) to the display module's <see cref="IDeviceDisplay"/> port, so an Android guest
/// gets screenshots and input with no change to the display module's wire or its checks (docs/design/62 section 5.1, work package 10).
/// </summary>
public sealed class DeviceDisplayAdapter : IDeviceDisplay
{
    private readonly IDeviceControl _control;

    public DeviceDisplayAdapter(IDeviceControl control) => _control = control ?? throw new ArgumentNullException(nameof(control));

    /// <inheritdoc />
    public bool Supports(DisplayTarget target) => target is { Os: DisplayGuestOs.Android };

    /// <inheritdoc />
    public async Task<byte[]> CaptureScreenshotPngAsync(DisplayTarget target, CancellationToken ct)
    {
        DeviceScreen screen = await _control.CaptureAsync(target.VmId, ct).ConfigureAwait(false);
        return screen.Png;
    }

    /// <inheritdoc />
    public async Task DispatchInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        // Map the whole batch first, so a refused action sends nothing at all.
        IReadOnlyList<DeviceInput> inputs = DeviceInputMapper.Map(actions);
        foreach (DeviceInput input in inputs)
            await _control.SendInputAsync(target.VmId, input, ct).ConfigureAwait(false);
    }
}
