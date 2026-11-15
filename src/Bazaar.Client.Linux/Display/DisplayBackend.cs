namespace Bazaar.Client.Linux.Display;

/// <summary>The windowing system the Linux client should use (doc 30, section 1: X11 by default, Wayland opt-in).</summary>
public enum DisplayBackend
{
    /// <summary>No display was found. The client cannot open a window.</summary>
    None,

    /// <summary>X11 through XWayland or a plain X server.</summary>
    X11,

    /// <summary>Native Wayland.</summary>
    Wayland,
}

/// <summary>
/// Chooses the display backend from the environment, pure so it can be tested without a session.
/// Order: an explicit <c>BAZAAR_DISPLAY_BACKEND</c> (<c>x11</c> or <c>wayland</c>) wins; otherwise a Wayland session
/// (<c>WAYLAND_DISPLAY</c> set, or <c>XDG_SESSION_TYPE=wayland</c>) selects Wayland; otherwise <c>DISPLAY</c> selects X11.
/// An unknown explicit value is ignored, so a typo never blocks start-up.
/// </summary>
public static class DisplayBackendSelector
{
    public const string OverrideVariable = "BAZAAR_DISPLAY_BACKEND";

    public static DisplayBackend Select(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        switch (environment(OverrideVariable)?.Trim().ToLowerInvariant())
        {
            case "x11":
                return DisplayBackend.X11;
            case "wayland":
                return DisplayBackend.Wayland;
        }

        if (!string.IsNullOrWhiteSpace(environment("WAYLAND_DISPLAY"))
            || string.Equals(environment("XDG_SESSION_TYPE")?.Trim(), "wayland", StringComparison.OrdinalIgnoreCase))
        {
            return DisplayBackend.Wayland;
        }

        return string.IsNullOrWhiteSpace(environment("DISPLAY")) ? DisplayBackend.None : DisplayBackend.X11;
    }
}
