using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Materials.Avalonia;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Materials.Tests;

public class MaterialApplierHeadlessTests
{
    private static readonly ThemeDefinition _void = BundledThemes.Load("void");

    private sealed class FixedSource(MaterialEnvironment env) : IMaterialEnvironmentSource
    {
        public MaterialEnvironment Current { get; } = env;
    }

    private static MaterialPolicy PolicyFor(MaterialEnvironment env) => new(new FixedSource(env));

    [AvaloniaFact]
    public void Windows_11_window_gets_a_mica_hint_and_the_translucent_tint()
    {
        var window = new Window();
        var env = new MaterialEnvironment(MaterialPlatform.Windows, MicaAvailable: true, AcrylicAvailable: true);

        MaterialApplier.ApplyToWindow(window, PolicyFor(env), _void);
        window.Show();

        Assert.Equal(WindowTransparencyLevel.Mica, window.TransparencyLevelHint[0]);
        var brush = Assert.IsType<SolidColorBrush>(window.Background);
        Assert.Equal(0x0B, brush.Color.R);
        Assert.Equal((byte)Math.Round(255 * 0.85), brush.Color.A);
    }

    [AvaloniaFact]
    public void Reduce_transparency_gives_an_opaque_hint_and_the_theme_fallback_colour()
    {
        var window = new Window();
        var env = new MaterialEnvironment(MaterialPlatform.Windows, MicaAvailable: true, ReduceTransparency: true);

        MaterialApplier.ApplyToWindow(window, PolicyFor(env), _void);
        window.Show();

        Assert.Equal([WindowTransparencyLevel.None], window.TransparencyLevelHint);
        var brush = Assert.IsType<SolidColorBrush>(window.Background);
        Assert.Equal(MaterialBrushes.ParseColour(_void.Tokens["material.fallback.chrome"]), brush.Color);
        Assert.Equal(255, brush.Color.A);
    }

    [AvaloniaFact]
    public void Message_list_panel_stays_opaque_on_every_theme()
    {
        var env = new MaterialEnvironment(MaterialPlatform.Linux, Level: MaterialLevel.Translucent, TransparencyAvailable: true);
        foreach (string name in BundledThemes.Names)
        {
            ThemeDefinition theme = BundledThemes.Load(name);
            var panel = new Grid();

            MaterialApplier.ApplyToSurface(panel, MaterialSurface.MessageList, PolicyFor(env), theme);

            var brush = Assert.IsType<SolidColorBrush>(panel.Background);
            Assert.Equal(255, brush.Color.A);
            Assert.Equal(MaterialBrushes.ParseColour(theme.Tokens["color.background.base"]), brush.Color);
        }
    }

    [Fact]
    public void Eight_digit_theme_colours_read_alpha_last()
    {
        Color c = MaterialBrushes.ParseColour("#11223380");

        Assert.Equal(Color.FromArgb(0x80, 0x11, 0x22, 0x33), c);
    }
}
