namespace Bazaar.Server.VmDisplay.Abstractions;

/// <summary>The server contract for screenshots and input (docs/design/59, section 4).</summary>
public interface IGuestDisplay
{
    /// <summary>Captures one normalised PNG frame.</summary>
    /// <exception cref="DisplayException">The call was refused, limited, invalid or no source produced a frame.</exception>
    Task<ScreenFrame> CaptureAsync(CaptureRequest request, CancellationToken ct);

    /// <summary>Sends a batch of input to the target.</summary>
    /// <exception cref="DisplayException">The call was refused, limited, invalid or the source failed.</exception>
    Task SendInputAsync(InputBatch batch, CancellationToken ct);

    /// <summary>The sources usable for <paramref name="target"/> now, in preference order.</summary>
    IReadOnlyList<DisplaySourceKind> AvailableSources(DisplayTarget target);
}

/// <summary>Reads a VM's virtual framebuffer and injects synthetic input. The host adapts each VM backend to this port.</summary>
public interface IHypervisorConsole
{
    /// <summary>Whether this adapter serves <paramref name="target"/>.</summary>
    bool Supports(DisplayTarget target);

    /// <summary>Reads the framebuffer, asking for at most the given size where the backend supports that.</summary>
    Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct);

    /// <summary>Injects the actions as synthetic keyboard and pointer events.</summary>
    Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct);
}

/// <summary>Runs a fixed argv in a VM and fetches a file, over the guest channel (docs/design/16). No shell is involved.</summary>
public interface IDisplayGuestExec
{
    /// <summary>Whether this adapter serves <paramref name="target"/>.</summary>
    bool Supports(DisplayTarget target);

    /// <summary>Runs <paramref name="argv"/> and returns its exit code.</summary>
    Task<int> ExecAsync(Guid vmId, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct);

    /// <summary>Copies a guest file into <paramref name="sink"/>.</summary>
    Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct);
}

/// <summary>Screenshot and input for a browser session. The host adapts the browser module to this port.</summary>
public interface IBrowserPageDriver
{
    /// <summary>Whether this driver serves the target's browser session.</summary>
    bool Supports(DisplayTarget target);

    /// <summary>Returns a PNG of the page.</summary>
    Task<byte[]> CaptureScreenshotPngAsync(string sessionId, CancellationToken ct);

    /// <summary>Dispatches the actions as page input.</summary>
    Task DispatchInputAsync(string sessionId, IReadOnlyList<InputAction> actions, CancellationToken ct);
}

/// <summary>Taint and kill-switch decisions (docs/design/15). The host supplies the real gate.</summary>
public interface IDisplayGate
{
    /// <summary>Decides whether a capture may happen.</summary>
    DisplayVerdict CheckCapture(DisplayTarget target);

    /// <summary>Decides whether the input may be sent. The actions are passed so the host can refuse typed secrets.</summary>
    DisplayVerdict CheckInput(DisplayTarget target, IReadOnlyList<InputAction> actions);
}

/// <summary>Writes audit rows. Rows carry sizes, source and hash, never pixels or typed text.</summary>
public interface IDisplayAuditSink
{
    /// <summary>A frame was returned to a caller.</summary>
    void Captured(DisplayTarget target, DisplaySourceKind source, int width, int height, int bytes, string sha256);

    /// <summary>Input was dispatched.</summary>
    void InputSent(DisplayTarget target, DisplaySourceKind source, int actionCount);

    /// <summary>A call was refused or rejected before reaching a source.</summary>
    void Refused(DisplayTarget target, string operation, string reason);
}

/// <summary>
/// Read contract: the size of a target's display before any downscaling, learned from the last capture that knew it. Pointer
/// coordinates are in this space, so a consumer can map a point from a downscaled frame onto the display. Returns
/// <see langword="null"/> when unknown: no capture yet, or a source that cannot tell (a hypervisor frame may already be reduced, and
/// a browser screenshot is in device pixels while its input is in CSS pixels).
/// </summary>
public interface IDisplayNativeSizeReader
{
    /// <summary>The native size of the target's display, or <see langword="null"/> when unknown.</summary>
    (int Width, int Height)? TryGetNativeSize(DisplayTarget target);
}
