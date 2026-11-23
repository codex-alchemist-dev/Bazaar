using Bazaar.Client.Materials;

namespace Bazaar.Client.Windows.Tests;

public class WindowsMaterialEnvironmentSourceTests
{
    [Theory]
    [InlineData(22631, true, true, true)]
    [InlineData(22000, true, true, true)]
    [InlineData(19045, false, true, true)]
    [InlineData(17134, false, true, true)]
    [InlineData(16299, false, false, true)]
    [InlineData(0, false, false, false)]
    public void The_os_build_decides_mica_acrylic_and_plain_transparency(int build, bool mica, bool acrylic, bool transparency)
    {
        MaterialEnvironment env = WindowsMaterialEnvironmentSource.Map(new WindowsPlatformFacts(build, TransparencyEnabled: true, BatterySaver: false));

        Assert.Equal(MaterialPlatform.Windows, env.Platform);
        Assert.Equal(mica, env.MicaAvailable);
        Assert.Equal(acrylic, env.AcrylicAvailable);
        Assert.Equal(transparency, env.TransparencyAvailable);
    }

    [Fact]
    public void Transparency_effects_off_is_reduce_transparency_and_wins_over_the_translucent_setting()
    {
        var source = new WindowsMaterialEnvironmentSource(() => new WindowsPlatformFacts(22631, TransparencyEnabled: false, BatterySaver: false));
        source.Settings.Level = MaterialLevel.Translucent;

        MaterialDecision decision = new MaterialPolicy(source).Resolve(MaterialSurface.Chrome);

        Assert.True(source.Current.ReduceTransparency);
        Assert.Equal(MaterialReason.ReduceTransparency, decision.Reason);
    }

    [Fact]
    public void Battery_saver_turns_chrome_opaque_unless_the_person_forces_translucency()
    {
        var source = new WindowsMaterialEnvironmentSource(() => new WindowsPlatformFacts(22631, TransparencyEnabled: true, BatterySaver: true));
        var policy = new MaterialPolicy(source);

        Assert.Equal(MaterialReason.BatterySaver, policy.Resolve(MaterialSurface.Chrome).Reason);

        source.Settings.Level = MaterialLevel.Translucent;
        Assert.Equal(MaterialKind.Mica, policy.Resolve(MaterialSurface.Chrome).Kind);
    }

    [Fact]
    public void Windows_11_with_everything_on_gives_mica_chrome_and_a_tinted_strip()
    {
        var policy = new MaterialPolicy(new WindowsMaterialEnvironmentSource(() => new WindowsPlatformFacts(22631, true, false)));

        Assert.Equal(MaterialKind.Mica, policy.Resolve(MaterialSurface.Chrome).Kind);
        Assert.Equal(MaterialKind.Tinted, policy.Resolve(MaterialSurface.Strip).Kind);
        Assert.True(policy.Resolve(MaterialSurface.MessageList).IsOpaque);
    }

    [Fact]
    public void Reading_the_live_os_does_not_throw_and_off_windows_reports_nothing_available()
    {
        WindowsPlatformFacts facts = WindowsPlatformFacts.Read();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(0, facts.BuildNumber);
            Assert.True(MaterialPolicy.Resolve(MaterialSurface.Chrome, WindowsMaterialEnvironmentSource.Map(facts)).IsOpaque);
        }
        else
        {
            Assert.True(facts.BuildNumber > 0);
        }
    }
}
