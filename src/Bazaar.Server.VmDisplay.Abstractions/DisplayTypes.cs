using System.Security.Cryptography;

namespace Bazaar.Server.VmDisplay.Abstractions;

/// <summary>Where a frame or an input event travels (docs/design/59, section 3).</summary>
public enum DisplaySourceKind
{
    /// <summary>The host reads the VM's virtual framebuffer and injects synthetic input.</summary>
    Hypervisor,

    /// <summary>The guest agent runs a fixed capture or input tool and returns the result over the guest channel.</summary>
    Guest,

    /// <summary>The browser's debugging protocol (page screenshot and page input).</summary>
    Browser,

    /// <summary>A device provider's own screen and input channel, for example adb for an Android guest (docs/design/62).</summary>
    Device,
}

/// <summary>The guest operating system family of a display target.</summary>
public enum DisplayGuestOs
{
    /// <summary>A Windows guest.</summary>
    Windows,

    /// <summary>A Linux guest.</summary>
    Linux,

    /// <summary>An Android emulator guest (docs/design/62). Served only by the <see cref="DisplaySourceKind.Device"/> source.</summary>
    Android,
}

/// <summary>Hard limits that settings may tighten but never loosen (docs/design/59, section 9).</summary>
public static class DisplayLimits
{
    /// <summary>Largest frame width ever accepted or produced.</summary>
    public const int CeilingWidth = 3840;

    /// <summary>Largest frame height ever accepted or produced.</summary>
    public const int CeilingHeight = 2160;
}

/// <summary>One agent's VM (or browser session in it) that a capture or input call addresses.</summary>
/// <param name="AgentId">The owning agent. A call names exactly one agent and one VM.</param>
/// <param name="VmId">The VM id.</param>
/// <param name="Os">The guest OS family.</param>
/// <param name="BackendRef">Opaque reference the host's hypervisor adapter resolves (for example a domain name or VM id).</param>
/// <param name="BrowserSessionId">The browser session to use for the browser source, when there is one.</param>
public sealed record DisplayTarget(string AgentId, Guid VmId, DisplayGuestOs Os, string BackendRef, string? BrowserSessionId = null);

/// <summary>A request for one screenshot.</summary>
/// <param name="Target">What to capture.</param>
/// <param name="Source">The only source to use, or <see langword="null"/> to let the module choose and fall back.</param>
public sealed record CaptureRequest(DisplayTarget Target, DisplaySourceKind? Source = null);

/// <summary>A normalised screenshot. Pixels are untrusted external content and may contain secrets (docs/design/59, section 5).</summary>
/// <param name="Png">The PNG bytes.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Source">The source that produced the frame.</param>
/// <param name="CapturedAt">When the frame was captured.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of <paramref name="Png"/>.</param>
public sealed record ScreenFrame(byte[] Png, int Width, int Height, DisplaySourceKind Source, DateTimeOffset CapturedAt, string Sha256)
{
    /// <summary>Describes the frame without its pixel bytes.</summary>
    public override string ToString() => $"ScreenFrame {{ {Width}x{Height}, {Png.Length} bytes, {Source}, sha256 {Sha256} }}";

    /// <summary>Lower-case hex SHA-256 of <paramref name="data"/>.</summary>
    public static string ComputeSha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>A pixel format a source may hand back as a raw frame.</summary>
public enum RawPixelFormat
{
    /// <summary>A complete PNG file.</summary>
    Png,

    /// <summary>Little-endian RGB565, two bytes per pixel (Hyper-V thumbnails).</summary>
    Rgb565,

    /// <summary>Blue, green, red, alpha, four bytes per pixel.</summary>
    Bgra32,

    /// <summary>A binary PPM file (P6, maxval 255), as older QEMU writes.</summary>
    Ppm,
}

/// <summary>A frame as a source delivered it, before the module validates and normalises it.</summary>
/// <param name="Format">The pixel format of <paramref name="Data"/>.</param>
/// <param name="Data">The bytes. They may come from a compromised guest.</param>
/// <param name="Width">Declared width for raw formats; ignored for <see cref="RawPixelFormat.Png"/> and <see cref="RawPixelFormat.Ppm"/>, which carry their own.</param>
/// <param name="Height">Declared height, as for <paramref name="Width"/>.</param>
public sealed record RawFrame(RawPixelFormat Format, byte[] Data, int Width, int Height)
{
    /// <summary>Describes the frame without its bytes.</summary>
    public override string ToString() => $"RawFrame {{ {Format}, {Data?.Length ?? 0} bytes, {Width}x{Height} }}";
}

/// <summary>An allow or refuse decision from the host's taint and kill-switch state (docs/design/15).</summary>
/// <param name="Allowed">Whether the call may go ahead.</param>
/// <param name="Reason">Why it was refused.</param>
public sealed record DisplayVerdict(bool Allowed, string? Reason)
{
    /// <summary>An allow verdict.</summary>
    public static DisplayVerdict Allow { get; } = new(true, null);

    /// <summary>A refuse verdict with a reason.</summary>
    public static DisplayVerdict Deny(string reason) => new(false, reason);
}

/// <summary>Why a display call failed.</summary>
public enum DisplayFailure
{
    /// <summary>The module is turned off.</summary>
    Disabled,

    /// <summary>The gate refused the call.</summary>
    Refused,

    /// <summary>The per-agent rate limit was reached.</summary>
    RateLimited,

    /// <summary>No usable source exists for the target (or the named source is unavailable).</summary>
    NoSourceAvailable,

    /// <summary>The source was tried and failed.</summary>
    SourceFailed,

    /// <summary>A frame exceeded the size limits and was rejected.</summary>
    FrameTooLarge,

    /// <summary>A frame was not a well-formed image of a supported kind.</summary>
    FrameMalformed,

    /// <summary>The call itself was invalid.</summary>
    InvalidInput,
}

/// <summary>Thrown by the display module with a typed reason.</summary>
public sealed class DisplayException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DisplayException(DisplayFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>The failure kind.</summary>
    public DisplayFailure Failure { get; }
}
