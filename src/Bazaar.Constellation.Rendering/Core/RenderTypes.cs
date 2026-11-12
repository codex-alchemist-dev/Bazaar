namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Native surface kinds a backend can attach to.</summary>
public enum RenderSurfaceKind
{
    /// <summary>Handles are HINSTANCE (PlatformDisplay) and HWND (PlatformWindow).</summary>
    Win32,

    /// <summary>Handles are Display* (PlatformDisplay) and Window (PlatformWindow).</summary>
    Xlib,

    /// <summary>Handles are wl_display* (PlatformDisplay) and wl_surface* (PlatformWindow).</summary>
    Wayland,

    /// <summary>Handle is ANativeWindow* (PlatformWindow); PlatformDisplay is unused.</summary>
    Android,

    /// <summary>No native window. Offscreen or test surface (VK_EXT_headless_surface); handles are unused.</summary>
    Headless,
}

/// <summary>A native window the backend draws into. The host owns the window's lifetime.</summary>
public readonly record struct RenderSurface(
    RenderSurfaceKind Kind,
    nint PlatformDisplay,
    nint PlatformWindow,
    int WidthPixels,
    int HeightPixels);

/// <summary>Outcome of the backend attach call (see the adapter interface).</summary>
public readonly record struct RenderAttachResult(bool Attached, string Reason);

/// <summary>What a backend can do. Optional features are only listed when the device reports them.</summary>
public sealed record RendererCapabilities(
    string Backend,
    string ApiVersion,
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> OptionalFeatures)
{
    /// <summary>A backend that has not probed, or found nothing usable.</summary>
    public static RendererCapabilities None { get; } = new("none", string.Empty, [], []);
}
