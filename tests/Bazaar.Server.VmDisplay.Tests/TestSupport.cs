using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.VmDisplay.Codecs;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDisplay.Tests;

/// <summary>A gate for tests only: allows everything unless told to deny.</summary>
internal sealed class AllowAllDisplayGate : IDisplayGate
{
    public string? DenyReason { get; set; }

    public bool Throw { get; set; }

    public DisplayVerdict CheckCapture(DisplayTarget target) => Verdict();

    public DisplayVerdict CheckInput(DisplayTarget target, IReadOnlyList<InputAction> actions) => Verdict();

    private DisplayVerdict Verdict()
    {
        if (Throw)
            throw new InvalidOperationException("gate exploded");
        return DenyReason is null ? DisplayVerdict.Allow : DisplayVerdict.Deny(DenyReason);
    }
}

internal sealed class RecordingAudit : IDisplayAuditSink
{
    public List<string> Rows { get; } = [];

    public List<(DisplaySourceKind Source, int Width, int Height, int Bytes, string Sha)> Captures { get; } = [];

    public void Captured(DisplayTarget target, DisplaySourceKind source, int width, int height, int bytes, string sha256)
    {
        Rows.Add("captured");
        Captures.Add((source, width, height, bytes, sha256));
    }

    public void InputSent(DisplayTarget target, DisplaySourceKind source, int actionCount) => Rows.Add($"input:{source}:{actionCount}");

    public void Refused(DisplayTarget target, string operation, string reason) => Rows.Add($"refused:{operation}:{reason}");
}

internal sealed class FakeHypervisor : IHypervisorConsole
{
    public bool Supported { get; set; } = true;

    public Func<RawFrame> Frame { get; set; } = () => throw new InvalidOperationException("no frame configured");

    public bool Fail { get; set; }

    public int Reads { get; private set; }

    public List<IReadOnlyList<InputAction>> Inputs { get; } = [];

    public (int W, int H)? LastRequestedSize { get; private set; }

    public bool Supports(DisplayTarget target) => Supported;

    public Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct)
    {
        Reads++;
        LastRequestedSize = (maxWidth, maxHeight);
        if (Fail)
            throw new IOException("hypervisor down");
        return Task.FromResult(Frame());
    }

    public Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        if (Fail)
            throw new IOException("hypervisor down");
        Inputs.Add(actions);
        return Task.CompletedTask;
    }
}

internal sealed class FakeGuestExec : IDisplayGuestExec
{
    public bool Supported { get; set; } = true;

    public List<IReadOnlyList<string>> Execs { get; } = [];

    public Func<IReadOnlyList<string>, int> ExitFor { get; set; } = _ => 0;

    public Action<string, Stream>? OnGetFile { get; set; }

    public bool GetFileFails { get; set; }

    public string? FetchedPath { get; private set; }

    public bool Supports(DisplayTarget target) => Supported;

    public Task<int> ExecAsync(Guid vmId, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        Execs.Add(argv);
        return Task.FromResult(ExitFor(argv));
    }

    public Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct)
    {
        FetchedPath = guestPath;
        if (GetFileFails)
            throw new IOException("channel dropped");
        OnGetFile?.Invoke(guestPath, sink);
        return Task.CompletedTask;
    }
}

/// <summary>Wraps the fake as a driver whose screenshot pixels are its input coordinates.</summary>
internal sealed class PixelExactBrowser(FakeBrowser inner) : IPixelExactBrowserPageDriver
{
    public bool Supports(DisplayTarget target) => inner.Supports(target);

    public Task<byte[]> CaptureScreenshotPngAsync(string sessionId, CancellationToken ct) => inner.CaptureScreenshotPngAsync(sessionId, ct);

    public Task DispatchInputAsync(string sessionId, IReadOnlyList<InputAction> actions, CancellationToken ct) =>
        inner.DispatchInputAsync(sessionId, actions, ct);
}

internal sealed class FakeBrowser : IBrowserPageDriver
{
    public bool Supported { get; set; } = true;

    public Func<byte[]> Png { get; set; } = () => throw new InvalidOperationException("no screenshot configured");

    public bool Fail { get; set; }

    public List<IReadOnlyList<InputAction>> Inputs { get; } = [];

    public string? LastSession { get; private set; }

    public bool Supports(DisplayTarget target) => Supported;

    public Task<byte[]> CaptureScreenshotPngAsync(string sessionId, CancellationToken ct)
    {
        LastSession = sessionId;
        if (Fail)
            throw new IOException("cdp closed");
        return Task.FromResult(Png());
    }

    public Task DispatchInputAsync(string sessionId, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        LastSession = sessionId;
        Inputs.Add(actions);
        return Task.CompletedTask;
    }
}

/// <summary>Builds a service with fakes and exposes them.</summary>
internal sealed class Rig
{
    public VmDisplayOptions Options { get; } = new();

    public AllowAllDisplayGate Gate { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

    public FakeHypervisor Hypervisor { get; } = new();

    public FakeGuestExec Guest { get; } = new();

    public FakeBrowser Browser { get; } = new();

    public bool UseHypervisor { get; set; } = true;

    public bool UseGuest { get; set; } = true;

    public bool UseBrowser { get; set; } = true;

    public bool PixelExactBrowser { get; set; }

    public GuestDisplayService Build() => new(
        Options,
        Gate,
        Audit,
        Time,
        UseHypervisor ? [Hypervisor] : [],
        UseGuest ? [Guest] : [],
        UseBrowser ? [PixelExactBrowser ? new PixelExactBrowser(Browser) : Browser] : []);

    public static DisplayTarget Linux(string? browserSession = null, string agent = "agent-1") =>
        new(agent, Guid.Parse("11111111-1111-1111-1111-111111111111"), DisplayGuestOs.Linux, "vm-linux", browserSession);

    public static DisplayTarget Windows(string? browserSession = null, string agent = "agent-1") =>
        new(agent, Guid.Parse("22222222-2222-2222-2222-222222222222"), DisplayGuestOs.Windows, "vm-win", browserSession);
}

internal static class Images
{
    /// <summary>A deterministic gradient so scaled output is predictable.</summary>
    public static PixelBuffer Gradient(int w, int h, int channels = 3)
    {
        var data = new byte[w * h * channels];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * channels;
                data[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                data[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                data[o + 2] = (byte)((x + y) % 256);
                if (channels == 4)
                    data[o + 3] = 255;
            }
        }
        return new PixelBuffer(w, h, channels, data);
    }

    public static byte[] Png(int w, int h, int channels = 3) => PngEncoder.Encode(Gradient(w, h, channels));

    public static byte[] Ppm(int w, int h, byte[]? pixels = null)
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        pixels ??= Gradient(w, h).Data;
        return [.. header, .. pixels];
    }
}
