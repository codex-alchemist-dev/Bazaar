using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class NightSkyThemeTests
{
    private static readonly NightSkyTheme _theme = NightSkyTheme.Default;

    [Fact]
    public void Background_is_pure_opaque_black()
    {
        Assert.Equal((0f, 0f, 0f, 1f), _theme.Background);
    }

    [Fact]
    public void Default_theme_of_the_adapter_is_the_night_sky()
    {
        Assert.Same(NightSkyTheme.Default, ConstellationNodeStyles.DefaultTheme);
    }

    [Fact]
    public void Empty_scene_is_just_the_void()
    {
        NodeStyleTable table = _theme.BuildStyles(ReadOnlySpan<float>.Empty);
        Assert.Equal(0, table.NodeCount);
        Assert.Equal(0, NodeStylePalette.Build(table).NodeCount);
        Assert.Equal((0f, 0f, 0f), (_theme.Background.R, _theme.Background.G, _theme.Background.B));
    }

    [Fact]
    public void Colour_ramp_runs_from_warm_to_cool()
    {
        Assert.Equal(_theme.WarmColor, _theme.ColorForImportance(0f));
        Assert.Equal(_theme.CoolColor, _theme.ColorForImportance(1f));
        var mid = _theme.ColorForImportance(0.5f);
        Assert.True(mid.R < _theme.WarmColor.R && mid.R > _theme.CoolColor.R);
        Assert.True(mid.B > _theme.WarmColor.B && mid.B < _theme.CoolColor.B);
    }

    [Theory]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(7f)]
    public void Importance_outside_the_range_is_clamped(float importance)
    {
        NodeStyle style = _theme.StyleForImportance(importance);
        Assert.InRange(style.Red, 0f, 1f);
        Assert.InRange(style.Green, 0f, 1f);
        Assert.InRange(style.Blue, 0f, 1f);
    }

    [Fact]
    public void Cluster_colour_is_stable_and_handles_negative_ids()
    {
        Assert.Equal(_theme.ColorForCluster(3), _theme.ColorForCluster(3));
        Assert.NotEqual(_theme.ColorForCluster(1), _theme.ColorForCluster(2));
        var c = _theme.ColorForCluster(-17);
        Assert.InRange(c.R, 0f, 1f);
    }

    [Fact]
    public void Build_styles_gives_one_style_per_node()
    {
        NodeStyleTable table = _theme.BuildStyles([0f, 1f, 0.5f]);
        Assert.Equal(3, table.NodeCount);
        Assert.Equal(_theme.StyleForImportance(1f), table.Get(1));
    }

    [Fact]
    public void Glow_rises_with_density_and_stays_bounded()
    {
        float lastScale = float.NegativeInfinity;
        float lastIntensity = float.NegativeInfinity;
        foreach (float density in new[] { -3f, 0f, 0.5f, 1f, 4f, 8f, 50f, 1e6f, float.MaxValue, float.PositiveInfinity })
        {
            float scale = _theme.GlowScale(density);
            float intensity = _theme.GlowIntensity(density);
            Assert.True(scale >= lastScale);
            Assert.True(intensity >= lastIntensity);
            Assert.InRange(scale, _theme.MinGlowScale, _theme.MaxGlowScale);
            Assert.InRange(intensity, _theme.MinGlowIntensity, _theme.MaxGlowIntensity);
            lastScale = scale;
            lastIntensity = intensity;
        }
    }

    [Fact]
    public void Glow_at_zero_density_is_the_minimum_and_nan_counts_as_zero()
    {
        Assert.Equal(_theme.MinGlowScale, _theme.GlowScale(0f));
        Assert.Equal(_theme.MinGlowIntensity, _theme.GlowIntensity(float.NaN));
    }

    [Fact]
    public void Twinkle_is_deterministic_and_within_bounds()
    {
        for (int node = -50; node < 200; node++)
        {
            for (double t = 0; t < 40; t += 0.37)
            {
                float a = _theme.Twinkle(node, t);
                Assert.Equal(a, _theme.Twinkle(node, t));
                Assert.InRange(a, 1f - _theme.TwinkleDepth, 1f);
            }
        }
    }

    [Fact]
    public void Twinkle_differs_between_nodes_and_over_time()
    {
        int byNode = Enumerable.Range(0, 64).Select(n => _theme.Twinkle(n, 1.3)).Distinct().Count();
        Assert.True(byNode > 8);
        int byTime = Enumerable.Range(0, 64).Select(i => _theme.Twinkle(5, i * 0.5)).Distinct().Count();
        Assert.True(byTime > 8);
    }

    [Fact]
    public void Twinkle_is_off_when_disabled_shedding_or_reduced_motion()
    {
        Assert.Equal(1f, _theme.Twinkle(4, 2.5, sheddingDetail: true));
        Assert.Equal(1f, _theme.Twinkle(4, 2.5, reducedMotion: true));
        Assert.Equal(1f, (_theme with { TwinkleEnabled = false }).Twinkle(4, 2.5));
        Assert.Equal(1f, _theme.Twinkle(4, double.NaN));
    }
}
