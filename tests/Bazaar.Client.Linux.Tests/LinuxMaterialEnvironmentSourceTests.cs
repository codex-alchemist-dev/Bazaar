using Bazaar.Client.Linux.Display;
using Bazaar.Client.Materials;

namespace Bazaar.Client.Linux.Tests;

public class LinuxMaterialEnvironmentSourceTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs) =>
        name => pairs.Where(p => p.Key == name).Select(p => p.Value).FirstOrDefault();

    [Fact]
    public void A_wayland_session_gets_a_tinted_chrome_and_no_blur_claim()
    {
        var facts = LinuxSessionFacts.Read(Env(("WAYLAND_DISPLAY", "wayland-0")), _ => null);
        var policy = new MaterialPolicy(new LinuxMaterialEnvironmentSource(() => facts));

        Assert.Equal(DisplayBackend.Wayland, facts.Backend);
        Assert.Equal(MaterialKind.Tinted, policy.Resolve(MaterialSurface.Chrome).Kind);
        Assert.True(policy.Resolve(MaterialSurface.Editor).IsOpaque);
    }

    [Fact]
    public void No_display_means_opaque()
    {
        var facts = LinuxSessionFacts.Read(Env(), _ => null);
        var policy = new MaterialPolicy(new LinuxMaterialEnvironmentSource(() => facts));

        Assert.Equal(MaterialReason.PlatformUnsupported, policy.Resolve(MaterialSurface.Chrome).Reason);
    }

    [Fact]
    public void A_low_power_platform_profile_is_battery_saver()
    {
        var facts = LinuxSessionFacts.Read(Env(("DISPLAY", ":0")), path => path == LinuxSessionFacts.PlatformProfilePath ? "low-power\n" : null);
        var policy = new MaterialPolicy(new LinuxMaterialEnvironmentSource(() => facts));

        Assert.True(facts.LowPowerProfile);
        Assert.Equal(MaterialReason.BatterySaver, policy.Resolve(MaterialSurface.Chrome).Reason);
    }

    [Fact]
    public void A_high_contrast_gtk_theme_stands_in_for_reduce_transparency()
    {
        var facts = LinuxSessionFacts.Read(Env(("DISPLAY", ":0"), ("GTK_THEME", "HighContrastInverse")), _ => "balanced");
        var policy = new MaterialPolicy(new LinuxMaterialEnvironmentSource(() => facts));

        Assert.Equal(MaterialReason.ReduceTransparency, policy.Resolve(MaterialSurface.Chrome).Reason);
    }

    [Fact]
    public void A_refused_backdrop_makes_the_chrome_opaque()
    {
        var source = new LinuxMaterialEnvironmentSource(() => new LinuxSessionFacts(DisplayBackend.X11, false, false));
        var policy = new MaterialPolicy(source);

        source.ReportBackdropFailed();

        Assert.Equal(MaterialReason.BackdropFailed, policy.Resolve(MaterialSurface.Chrome).Reason);
    }

    [Fact]
    public void Reading_the_live_session_does_not_throw()
    {
        _ = new LinuxMaterialEnvironmentSource().Current;
    }
}
