using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.VmDisplay.Codecs;

namespace Bazaar.Server.VmDisplay;

/// <summary>
/// The <see cref="IGuestDisplay"/> implementation (docs/design/59, sections 3 to 5). Checks run in this order: enabled, target,
/// gate, rate limit, then source selection. Ports are optional: the module works with any subset of them.
/// </summary>
public sealed class GuestDisplayService : IGuestDisplay, IDisplayNativeSizeReader
{
    /// <summary>The most VM displays whose native size is remembered; the oldest entry is dropped first.</summary>
    public const int MaxRememberedDisplays = 1024;

    private readonly VmDisplayOptions _options;
    private readonly IDisplayGate _gate;
    private readonly IDisplayAuditSink _audit;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<IHypervisorConsole> _hypervisors;
    private readonly IReadOnlyList<IDisplayGuestExec> _guestExecs;
    private readonly IReadOnlyList<IBrowserPageDriver> _browsers;
    private readonly IReadOnlyList<IDeviceDisplay> _devices;
    // The native size of the last guest-source frame per VM: the extent the guest pointer tool maps coordinates onto.
    private readonly ConcurrentDictionary<Guid, (int Width, int Height)> _guestSizes = new();
    // The native size of the last guest-source frame per VM: the extent the guest pointer tool maps coordinates onto. Bounded.
    private readonly Dictionary<Guid, (int Width, int Height)> _guestSizes = [];
    private readonly Queue<Guid> _guestSizeOrder = new();
    // The native size of the last screenshot per browser session, for pixel-exact drivers only. Bounded the same way.
    private readonly Dictionary<string, (int Width, int Height)> _browserSizes = new(StringComparer.Ordinal);
    private readonly Queue<string> _browserSizeOrder = new();
    private readonly object _guestSizeLock = new();
    private readonly SlidingWindowRateLimiter _captureLimiter;
    private readonly SlidingWindowRateLimiter _inputLimiter;

    /// <summary>Creates the service. Empty port lists simply mean those sources are unavailable.</summary>
    public GuestDisplayService(
        VmDisplayOptions options,
        IDisplayGate gate,
        IDisplayAuditSink audit,
        TimeProvider time,
        IEnumerable<IHypervisorConsole> hypervisors,
        IEnumerable<IDisplayGuestExec> guestExecs,
        IEnumerable<IBrowserPageDriver> browsers,
        IEnumerable<IDeviceDisplay>? devices = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _hypervisors = (hypervisors ?? []).ToArray();
        _guestExecs = (guestExecs ?? []).ToArray();
        _browsers = (browsers ?? []).ToArray();
        _devices = (devices ?? []).ToArray();
        _captureLimiter = new SlidingWindowRateLimiter(time, options.CapturesPerMinute, TimeSpan.FromMinutes(1));
        _inputLimiter = new SlidingWindowRateLimiter(time, options.InputBatchesPerMinute, TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public IReadOnlyList<DisplaySourceKind> AvailableSources(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_options.Enabled)
            return [];

        var sources = new List<DisplaySourceKind>(3);
        if (target.Os == DisplayGuestOs.Android)
        {
            // A device guest has no hypervisor framebuffer or guest agent here: only its provider's own channel serves it.
            if (_devices.Any(d => d.Supports(target)))
                sources.Add(DisplaySourceKind.Device);
            return sources;
        }

        if (!string.IsNullOrEmpty(target.BrowserSessionId) && _browsers.Any(b => b.Supports(target)))
            sources.Add(DisplaySourceKind.Browser);

        bool hasGuest = target.Os == DisplayGuestOs.Linux && _guestExecs.Any(e => e.Supports(target));
        bool hasHypervisor = _hypervisors.Any(h => h.Supports(target));
        if (target.Os == DisplayGuestOs.Linux)
        {
            if (hasGuest)
                sources.Add(DisplaySourceKind.Guest);
            if (hasHypervisor)
                sources.Add(DisplaySourceKind.Hypervisor);
        }
        else if (hasHypervisor)
        {
            sources.Add(DisplaySourceKind.Hypervisor);
        }
        return sources;
    }

    /// <inheritdoc />
    public async Task<ScreenFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        DisplayTarget target = request.Target ?? throw new ArgumentNullException(nameof(request));

        EnsureEnabled(target, "capture");
        Guard(target, "capture", () => InputValidator.ValidateTarget(target));
        DisplayVerdict verdict = await GateAsync(() => _gate.CheckCaptureAsync(target, ct));
        if (!verdict.Allowed)
            throw Refuse(target, "capture", DisplayFailure.Refused, verdict.Reason ?? "refused by the display gate");
        if (!_captureLimiter.TryAcquire(target.AgentId))
            throw Refuse(target, "capture", DisplayFailure.RateLimited, "the capture rate limit was reached");

        IReadOnlyList<DisplaySourceKind> order = Plan(target, request.Source);
        if (order.Count == 0)
            throw Refuse(target, "capture", DisplayFailure.NoSourceAvailable, request.Source is { } named
                ? $"the {named} source is not available for this target"
                : "no display source is available for this target");

        var limits = new FrameLimits(_options.EffectiveMaxWidth, _options.EffectiveMaxHeight, _options.MaxEncodedBytes);
        DisplayException? last = null;
        foreach (DisplaySourceKind source in order)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                NormalizedFrame frame = await ObtainAsync(source, target, limits, ct).ConfigureAwait(false);
                string sha = ScreenFrame.ComputeSha256(frame.Png);
                _audit.Captured(target, source, frame.Width, frame.Height, frame.Png.Length, sha);
                return new ScreenFrame(frame.Png, frame.Width, frame.Height, source, _time.GetUtcNow(), sha);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DisplayException e)
            {
                last = e;
            }
            catch (Exception e)
            {
                last = new DisplayException(DisplayFailure.SourceFailed, $"The {source} source failed: {e.GetType().Name}.", e);
            }
        }
        throw last!;
    }

    /// <inheritdoc />
    public async Task SendInputAsync(InputBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        DisplayTarget target = batch.Target ?? throw new ArgumentNullException(nameof(batch));

        EnsureEnabled(target, "input");
        Guard(target, "input", () =>
        {
            InputValidator.ValidateTarget(target);
            InputValidator.ValidateBatch(batch.Actions, _options);
        });
        IReadOnlyList<InputAction> actions = batch.Actions;
        DisplayVerdict verdict = await GateAsync(() => _gate.CheckInputAsync(target, actions, ct));
        if (!verdict.Allowed)
            throw Refuse(target, "input", DisplayFailure.Refused, verdict.Reason ?? "refused by the display gate");
        if (!_inputLimiter.TryAcquire(target.AgentId))
            throw Refuse(target, "input", DisplayFailure.RateLimited, "the input rate limit was reached");

        bool hasPointer = actions.Any(a => a is PointerMove or PointerClick or PointerScroll or PointerButton);
        (int Width, int Height)? guestSize = GuestSize(target.VmId);
        if (hasPointer && batch.Source == DisplaySourceKind.Guest && guestSize is null)
            throw Refuse(target, "input", DisplayFailure.InvalidInput, LinuxInputArgv.PointerRefusal);
        IReadOnlyList<DisplaySourceKind> order = Plan(target, batch.Source);
        // The guest pointer tool needs the output size, learned from a capture, so an unnamed pointer batch skips the guest source until then.
        if (hasPointer && guestSize is null)
            order = [.. order.Where(k => k != DisplaySourceKind.Guest)];
        if (order.Count == 0)
            throw Refuse(target, "input", DisplayFailure.NoSourceAvailable, batch.Source is { } named
                ? $"the {named} source is not available for this target"
                : hasPointer ? "no display source with pointer input is available for this target" : "no display source is available for this target");

        // Input is not idempotent, so it goes to the first source only and never falls back after a failure.
        DisplaySourceKind source = order[0];
        TimeSpan timeout = TimeSpan.FromSeconds(_options.GuestCaptureTimeoutSeconds);
        try
        {
            switch (source)
            {
                case DisplaySourceKind.Browser:
                    await _browsers.First(b => b.Supports(target)).DispatchInputAsync(target.BrowserSessionId!, actions, ct).ConfigureAwait(false);
                    break;
                case DisplaySourceKind.Hypervisor:
                    await _hypervisors.First(h => h.Supports(target)).SendInputAsync(target, actions, ct).ConfigureAwait(false);
                    break;
                case DisplaySourceKind.Device:
                    await _devices.First(d => d.Supports(target)).DispatchInputAsync(target, actions, ct).ConfigureAwait(false);
                    break;
                default:
                    await GuestSource.SendInputAsync(_guestExecs.First(e => e.Supports(target)), target, actions, guestSize, timeout, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DisplayException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, $"The {source} source failed: {e.GetType().Name}.", e);
        }
        _audit.InputSent(target, source, actions.Count);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The guest source records a size per VM. A browser target (one that names a session) has its own size, recorded only when its
    /// driver is an <see cref="IPixelExactBrowserPageDriver"/>; with any other driver the screenshot may be in device pixels while input
    /// is in CSS pixels, so it stays unknown. A browser target never falls back to its VM's size. A hypervisor frame may already be reduced.
    /// </remarks>
    public (int Width, int Height)? TryGetNativeSize(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.IsNullOrEmpty(target.BrowserSessionId) ? GuestSize(target.VmId) : BrowserSize(target.BrowserSessionId);
    }

    private (int Width, int Height)? BrowserSize(string sessionId)
    {
        lock (_guestSizeLock)
            return _browserSizes.TryGetValue(sessionId, out (int Width, int Height) known) ? known : null;
    }

    private void RememberBrowserSize(string sessionId, int width, int height)
    {
        lock (_guestSizeLock)
        {
            if (!_browserSizes.ContainsKey(sessionId))
            {
                _browserSizeOrder.Enqueue(sessionId);
                while (_browserSizeOrder.Count > MaxRememberedDisplays)
                    _browserSizes.Remove(_browserSizeOrder.Dequeue());
            }
            _browserSizes[sessionId] = (width, height);
        }
    }

    private (int Width, int Height)? GuestSize(Guid vmId)
    {
        lock (_guestSizeLock)
            return _guestSizes.TryGetValue(vmId, out (int Width, int Height) known) ? known : null;
    }

    private void RememberGuestSize(Guid vmId, int width, int height)
    {
        lock (_guestSizeLock)
        {
            if (!_guestSizes.ContainsKey(vmId))
            {
                _guestSizeOrder.Enqueue(vmId);
                while (_guestSizeOrder.Count > MaxRememberedDisplays)
                    _guestSizes.Remove(_guestSizeOrder.Dequeue());
            }
            _guestSizes[vmId] = (width, height);
        }
    }

    private async Task<NormalizedFrame> ObtainAsync(DisplaySourceKind source, DisplayTarget target, FrameLimits limits, CancellationToken ct)
    {
        switch (source)
        {
            case DisplaySourceKind.Browser:
                {
                    IBrowserPageDriver driver = _browsers.First(b => b.Supports(target));
                    byte[] png = await driver.CaptureScreenshotPngAsync(target.BrowserSessionId!, ct).ConfigureAwait(false);
                    if (driver is IPixelExactBrowserPageDriver)
                    {
                        // Screenshot pixels are input coordinates for this driver, so the size before downscaling is the click space.
                        PngInfo native = PngInspector.Inspect(png, limits.MaxEncodedBytes);
                        RememberBrowserSize(target.BrowserSessionId!, native.Width, native.Height);
                    }
                    return FrameNormalizer.FromPng(png, limits);
                }
            case DisplaySourceKind.Device:
                {
                    IDeviceDisplay device = _devices.First(d => d.Supports(target));
                    byte[] png = await device.CaptureScreenshotPngAsync(target, ct).ConfigureAwait(false);
                    return FrameNormalizer.FromPng(png, limits);
                }
            case DisplaySourceKind.Hypervisor:
                {
                    IHypervisorConsole console = _hypervisors.First(h => h.Supports(target));
                    RawFrame raw = await console.ReadFramebufferAsync(target, limits.MaxWidth, limits.MaxHeight, ct).ConfigureAwait(false);
                    return FrameNormalizer.FromRaw(raw, limits);
                }
            default:
                {
                    IDisplayGuestExec exec = _guestExecs.First(e => e.Supports(target));
                    byte[] png = await GuestSource.CaptureAsync(exec, target, TimeSpan.FromSeconds(_options.GuestCaptureTimeoutSeconds), limits.MaxEncodedBytes, ct).ConfigureAwait(false);
                    // The size before any downscaling is the guest's output size, which is what pointer coordinates refer to.
                    PngInfo native = PngInspector.Inspect(png, limits.MaxEncodedBytes);
                    RememberGuestSize(target.VmId, native.Width, native.Height);
                    return FrameNormalizer.FromPng(png, limits);
                }
        }
    }

    // A named source is the only one tried; otherwise the full preference order.
    private IReadOnlyList<DisplaySourceKind> Plan(DisplayTarget target, DisplaySourceKind? named)
    {
        IReadOnlyList<DisplaySourceKind> available = AvailableSources(target);
        if (named is null)
            return available;
        return available.Contains(named.Value) ? [named.Value] : [];
    }

    private void EnsureEnabled(DisplayTarget target, string operation)
    {
        if (!_options.Enabled)
            throw Refuse(target, operation, DisplayFailure.Disabled, "the display module is disabled");
    }

    private void Guard(DisplayTarget target, string operation, Action validate)
    {
        try
        {
            validate();
        }
        catch (DisplayException e)
        {
            throw Refuse(target, operation, e.Failure, e.Message);
        }
    }

    // A gate that throws is treated as a refusal: the module fails closed.
    private static async Task<DisplayVerdict> GateAsync(Func<ValueTask<DisplayVerdict>> check)
    {
        try
        {
            return await check().ConfigureAwait(false) ?? DisplayVerdict.Deny("the display gate returned no verdict");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DisplayVerdict.Deny("the display gate failed");
        }
    }

    private DisplayException Refuse(DisplayTarget target, string operation, DisplayFailure failure, string reason)
    {
        try
        {
            _audit.Refused(target, operation, reason);
        }
        catch (Exception)
        {
            // The call is already being refused; a failing audit sink must not change that.
        }
        return new DisplayException(failure, $"Display {operation} refused: {reason}.");
    }
}
