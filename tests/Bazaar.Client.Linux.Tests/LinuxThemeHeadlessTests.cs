using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Commands;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Linux.Tests;

public class LinuxThemeHeadlessTests
{
    private const string _background = "Bazaar.color.background.base";

    private static Color BackgroundOf(Application app) => Assert.IsType<Color>(Lookup(app, _background));

    private static object Lookup(Application app, string key)
    {
        Assert.True(app.TryGetResource(key, null, out var value), $"{key} missing");
        return value!;
    }

    [AvaloniaTheory]
    [InlineData("void")]
    [InlineData("graphite")]
    [InlineData("paper")]
    public void Each_bundled_theme_lands_in_application_resources(string name)
    {
        var app = Application.Current!;
        var applied = LinuxTheme.Apply(app, name, _ => Assert.Fail("no log expected"));

        Assert.Equal(name, applied);
        Assert.Equal(name, LinuxTheme.Current);
        foreach (var spec in ThemeTokens.Required)
        {
            Assert.True(app.TryGetResource("Bazaar." + spec.Name, null, out _), spec.Name);
        }

        Assert.IsType<SolidColorBrush>(Lookup(app, "Bazaar.brush.color.background.base"));
    }

    [AvaloniaFact]
    public void Resource_keys_match_the_generated_xaml()
    {
        var theme = BundledThemes.Load("graphite");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expected = XDocument.Parse(AvaloniaThemeGenerator.Generate(theme)).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().Order(StringComparer.Ordinal).ToArray();

        var actual = LinuxTheme.Build(theme).Keys.Cast<object>().Select(k => (string)k).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [AvaloniaFact]
    public void Unknown_name_falls_back_to_void_and_logs()
    {
        var logs = new List<string>();

        var applied = LinuxTheme.Apply(Application.Current, "neon", logs.Add);

        Assert.Equal("void", applied);
        Assert.Equal(Colors.Black, BackgroundOf(Application.Current!));
        Assert.Contains(logs, l => l.Contains("neon", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Runtime_switch_changes_a_token_and_keeps_one_dictionary()
    {
        var app = Application.Current!;
        var before = app.Resources.MergedDictionaries.Count;
        LinuxTheme.Apply(app, "void", _ => { });
        var voidBackground = BackgroundOf(app);

        Assert.Equal("paper", LinuxThemeCommands.Run(LinuxThemeCommands.PaperId, app, _ => { }));
        var paperBackground = BackgroundOf(app);

        Assert.NotEqual(voidBackground, paperBackground);
        Assert.True(app.Resources.MergedDictionaries.Count <= before + 1);
        Assert.Equal("graphite", LinuxThemeCommands.Run(LinuxThemeCommands.GraphiteId, app, _ => { }));
        Assert.Equal("void", LinuxThemeCommands.Run(LinuxThemeCommands.VoidId, app, _ => { }));
        Assert.Equal(voidBackground, BackgroundOf(app));
    }

    [AvaloniaFact]
    public void Dynamic_resource_follows_a_switch()
    {
        var app = Application.Current!;
        LinuxTheme.Apply(app, "void", _ => { });
        var border = new Border();
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Bazaar.brush.color.background.base"));
        var window = new Window { Content = border };
        window.Show();
        var before = Assert.IsType<SolidColorBrush>(border.Background).Color;

        LinuxTheme.Apply(app, "paper", _ => { });

        Assert.NotEqual(before, Assert.IsType<SolidColorBrush>(border.Background).Color);
        window.Close();
    }

    [Fact]
    public void Theme_commands_are_local_and_unknown_ids_are_ignored()
    {
        Assert.Equal(["theme.void", "theme.graphite", "theme.paper"], LinuxThemeCommands.All.Select(c => c.Id));
        Assert.All(LinuxThemeCommands.All, c => Assert.Equal(CommandSource.Local, c.Source));
        Assert.Null(LinuxThemeCommands.Run("update.check", null, _ => { }));
    }

    [Fact]
    public void Name_resolution_prefers_environment_then_flag_then_preference_then_default()
    {
        string? Env(string key) => key == "BAZAAR_THEME" ? "paper" : null;
        string? NoEnv(string key) => null;

        Assert.Equal("paper", LinuxTheme.ResolveName(["--theme", "graphite"], Env));
        Assert.Equal("graphite", LinuxTheme.ResolveName(["--theme", "graphite"], NoEnv));
        Assert.Equal("graphite", LinuxTheme.ResolveName(["--theme=graphite"], NoEnv));
        Assert.Equal("paper", LinuxTheme.ResolveName([], NoEnv, "paper"));
        Assert.Equal("void", LinuxTheme.ResolveName([], NoEnv));
        Assert.Equal("void", LinuxTheme.ResolveName(["--theme"], NoEnv));
    }
}
