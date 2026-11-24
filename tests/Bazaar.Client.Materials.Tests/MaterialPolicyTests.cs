namespace Bazaar.Client.Materials.Tests;

public class MaterialPolicyTests
{
    private static MaterialDecision Resolve(MaterialSurface surface, MaterialEnvironment env) => MaterialPolicy.Resolve(surface, env);

    private static readonly MaterialEnvironment _windows11 =
        new(MaterialPlatform.Windows, MicaAvailable: true, AcrylicAvailable: true, TransparencyAvailable: true);

    [Theory]
    [InlineData(MaterialSurface.MessageList)]
    [InlineData(MaterialSurface.Editor)]
    [InlineData(MaterialSurface.Terminal)]
    [InlineData(MaterialSurface.Diff)]
    [InlineData(MaterialSurface.LongText)]
    public void Content_surfaces_are_opaque_even_when_translucency_is_forced(MaterialSurface surface)
    {
        MaterialDecision d = Resolve(surface, _windows11 with { Level = MaterialLevel.Translucent });

        Assert.Equal(MaterialKind.Opaque, d.Kind);
        Assert.Equal(MaterialReason.ContentSurface, d.Reason);
    }

    [Fact]
    public void Windows_chrome_prefers_mica_then_acrylic_then_tinted_then_opaque()
    {
        Assert.Equal(MaterialKind.Mica, Resolve(MaterialSurface.Chrome, _windows11).Kind);
        Assert.Equal(MaterialKind.Acrylic, Resolve(MaterialSurface.Chrome, _windows11 with { MicaAvailable = false }).Kind);
        Assert.Equal(
            MaterialKind.Tinted,
            Resolve(MaterialSurface.Chrome, new MaterialEnvironment(MaterialPlatform.Windows, TransparencyAvailable: true)).Kind);
        MaterialDecision none = Resolve(MaterialSurface.Chrome, new MaterialEnvironment(MaterialPlatform.Windows));
        Assert.Equal(MaterialKind.Opaque, none.Kind);
        Assert.Equal(MaterialReason.PlatformUnsupported, none.Reason);
    }

    [Theory]
    [InlineData(MaterialSurface.Strip)]
    [InlineData(MaterialSurface.Sheet)]
    [InlineData(MaterialSurface.Overlay)]
    [InlineData(MaterialSurface.ToolWindow)]
    public void Windows_secondary_chrome_is_a_tinted_surface_over_the_backdrop(MaterialSurface surface) =>
        Assert.Equal(MaterialKind.Tinted, Resolve(surface, _windows11).Kind);

    [Fact]
    public void Linux_is_tinted_when_the_compositor_allows_transparency_else_opaque()
    {
        Assert.Equal(
            MaterialKind.Tinted,
            Resolve(MaterialSurface.Chrome, new MaterialEnvironment(MaterialPlatform.Linux, TransparencyAvailable: true)).Kind);
        Assert.Equal(MaterialKind.Opaque, Resolve(MaterialSurface.Chrome, new MaterialEnvironment(MaterialPlatform.Linux)).Kind);
    }

    [Fact]
    public void Android_is_tinted_and_claims_blur_only_when_it_was_measured_within_budget()
    {
        Assert.Equal(MaterialKind.Tinted, Resolve(MaterialSurface.Sheet, new MaterialEnvironment(MaterialPlatform.Android)).Kind);
        Assert.Equal(
            MaterialKind.Acrylic,
            Resolve(MaterialSurface.Sheet, new MaterialEnvironment(MaterialPlatform.Android, BlurMeasuredWithinBudget: true)).Kind);
    }

    [Theory]
    [InlineData(MaterialReason.ReduceTransparency)]
    [InlineData(MaterialReason.BatterySaver)]
    [InlineData(MaterialReason.BackdropFailed)]
    [InlineData(MaterialReason.FrameBudgetMissed)]
    [InlineData(MaterialReason.SettingOpaque)]
    public void Fallback_conditions_force_opaque(MaterialReason reason)
    {
        MaterialEnvironment env = reason switch
        {
            MaterialReason.ReduceTransparency => _windows11 with { ReduceTransparency = true },
            MaterialReason.BatterySaver => _windows11 with { BatterySaver = true },
            MaterialReason.BackdropFailed => _windows11 with { BackdropRequestFailed = true },
            MaterialReason.FrameBudgetMissed => _windows11 with { FrameBudgetMissed = true },
            _ => _windows11 with { Level = MaterialLevel.Opaque },
        };

        MaterialDecision d = Resolve(MaterialSurface.Chrome, env);

        Assert.True(d.IsOpaque);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void Forced_translucent_overrides_battery_saver_and_frame_miss_but_not_reduce_transparency_or_a_failed_backdrop()
    {
        MaterialEnvironment forced = _windows11 with { Level = MaterialLevel.Translucent, BatterySaver = true, FrameBudgetMissed = true };

        Assert.Equal(MaterialKind.Mica, Resolve(MaterialSurface.Chrome, forced).Kind);
        Assert.True(Resolve(MaterialSurface.Chrome, forced with { ReduceTransparency = true }).IsOpaque);
        Assert.True(Resolve(MaterialSurface.Chrome, forced with { BackdropRequestFailed = true }).IsOpaque);
    }

    [Fact]
    public void Policy_reads_the_environment_each_time()
    {
        var source = new MutableSource(_windows11);
        var policy = new MaterialPolicy(source);

        Assert.Equal(MaterialKind.Mica, policy.Resolve(MaterialSurface.Chrome).Kind);
        source.Current = _windows11 with { FrameBudgetMissed = true };
        Assert.Equal(MaterialReason.FrameBudgetMissed, policy.Resolve(MaterialSurface.Chrome).Reason);
    }

    [Fact]
    public void Setting_key_and_values_round_trip()
    {
        Assert.Equal("client.materials", MaterialSettings.Key);
        Assert.Equal(MaterialLevel.Auto, MaterialSettings.Default);
        foreach (MaterialLevel level in Enum.GetValues<MaterialLevel>())
        {
            Assert.Equal(level, MaterialSettings.Parse(MaterialSettings.Format(level)));
        }

        Assert.Equal(MaterialLevel.Opaque, MaterialSettings.Parse(" OPAQUE "));
        Assert.Equal(MaterialLevel.Auto, MaterialSettings.Parse("bogus"));
        Assert.Equal(MaterialLevel.Auto, MaterialSettings.Parse(null));
    }

    private sealed class MutableSource(MaterialEnvironment current) : IMaterialEnvironmentSource
    {
        public MaterialEnvironment Current { get; set; } = current;
    }
}
