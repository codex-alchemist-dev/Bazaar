using Bazaar.Client.Linux.Display;

namespace Bazaar.Client.Linux.Tests;

public class DisplayBackendSelectorTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    [Fact]
    public void Wayland_display_selects_wayland_even_when_x_is_also_present()
    {
        var backend = DisplayBackendSelector.Select(Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")));

        Assert.Equal(DisplayBackend.Wayland, backend);
    }

    [Fact]
    public void Session_type_wayland_selects_wayland()
    {
        Assert.Equal(DisplayBackend.Wayland, DisplayBackendSelector.Select(Env(("XDG_SESSION_TYPE", "Wayland"))));
    }

    [Fact]
    public void Display_alone_selects_x11()
    {
        Assert.Equal(DisplayBackend.X11, DisplayBackendSelector.Select(Env(("DISPLAY", ":0"))));
    }

    [Fact]
    public void No_display_variables_selects_none()
    {
        Assert.Equal(DisplayBackend.None, DisplayBackendSelector.Select(Env()));
    }

    [Fact]
    public void Explicit_override_beats_the_session_detection()
    {
        var backend = DisplayBackendSelector.Select(Env(
            ("BAZAAR_DISPLAY_BACKEND", "x11"), ("WAYLAND_DISPLAY", "wayland-0")));

        Assert.Equal(DisplayBackend.X11, backend);
    }

    [Fact]
    public void Explicit_wayland_override_works_without_a_wayland_session()
    {
        Assert.Equal(DisplayBackend.Wayland, DisplayBackendSelector.Select(Env(("BAZAAR_DISPLAY_BACKEND", " WAYLAND "))));
    }

    [Fact]
    public void Unknown_override_is_ignored()
    {
        Assert.Equal(DisplayBackend.X11, DisplayBackendSelector.Select(Env(("BAZAAR_DISPLAY_BACKEND", "mir"), ("DISPLAY", ":0"))));
    }

    [Fact]
    public void Null_environment_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DisplayBackendSelector.Select(null!));
    }
}
