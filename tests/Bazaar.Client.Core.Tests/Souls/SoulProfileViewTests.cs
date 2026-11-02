using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Core.Souls;
using Bazaar.Client.Core.Tests.Theme;
using Bazaar.Client.Core.Theme;
using Bazaar.Contracts.Soul;

namespace Bazaar.Client.Core.Tests.Souls;

public class SoulProfileViewTests
{
    private static readonly Guid _soul = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");

    private static SoulProfileViewModel Model(params int[] versions)
    {
        var chain = versions.Select(v => new SoulVersion(
            _soul,
            v,
            v == 1 ? null : v - 1,
            new string('c', 64),
            "{\"tone\":\"dry\"}",
            new string('d', 64),
            "dom",
            "reason " + v,
            null,
            new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero).AddHours(v))).ToList();

        return new SoulProfileViewModel(new SoulProfile(_soul, "Charles", "Architect", "Haiku 5.5"), chain);
    }

    /// <summary>Builds a window with the light theme's resources under the keys the generator emits.</summary>
    private static Window HostWindow(object content)
    {
        ThemeLoadResult loaded = ThemeValidator.Validate(ThemeFixtures.Read("light.theme.json"));
        Assert.True(loaded.IsValid);

        var window = new Window { Width = 480, Height = 800, Content = content };
        window.Resources.MergedDictionaries.Add(ThemeResources(loaded.Definition!));
        return window;
    }

    /// <summary>
    /// The same keys and types <see cref="AvaloniaThemeGenerator"/> writes as XAML, built in code because
    /// Avalonia 12 has no runtime XAML parser in this build.
    /// </summary>
    private static ResourceDictionary ThemeResources(ThemeDefinition theme)
    {
        var dictionary = new ResourceDictionary();
        foreach (TokenSpec spec in ThemeTokens.Required)
        {
            string value = theme.Tokens[spec.Name];
            string key = "Bazaar." + spec.Name;
            switch (spec.Kind)
            {
                case TokenKind.Colour:
                    var color = Color.Parse(value);
                    dictionary[key] = color;
                    dictionary["Bazaar.brush." + spec.Name] = new SolidColorBrush(color);
                    break;
                case TokenKind.FontFamily:
                    dictionary[key] = new FontFamily(value);
                    break;
                case TokenKind.Length:
                    dictionary[key] = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case TokenKind.Flag:
                    dictionary[key] = bool.Parse(value);
                    break;
            }
        }

        return dictionary;
    }

    [AvaloniaFact]
    public void Card_shows_version_edited_marker_and_history_rows()
    {
        var view = new SoulProfileView { DataContext = Model(1, 2, 3) };
        var window = HostWindow(view);
        window.Show();

        Assert.Equal("v3", view.FindControl<TextBlock>("VersionText")!.Text);
        Assert.True(view.FindControl<TextBlock>("EditedMarker")!.IsVisible);
        Assert.Equal("sha256 dddddddddddd", view.FindControl<TextBlock>("HashText")!.Text);
        Assert.Equal("Preferences: tone: dry", view.FindControl<TextBlock>("PreferencesText")!.Text);
        Assert.False(view.FindControl<TextBlock>("EmptyHistoryText")!.IsVisible);
        Assert.Equal(3, view.FindControl<ItemsControl>("HistoryList")!.ItemCount);
    }

    [AvaloniaFact]
    public void Fresh_soul_hides_the_edited_marker()
    {
        var view = new SoulProfileView { DataContext = Model(1) };
        var window = HostWindow(view);
        window.Show();

        Assert.False(view.FindControl<TextBlock>("EditedMarker")!.IsVisible);
        Assert.Equal("v1", view.FindControl<TextBlock>("VersionText")!.Text);
    }

    [AvaloniaFact]
    public void Soul_without_versions_shows_the_empty_history_note()
    {
        var empty = new SoulProfileViewModel(new SoulProfile(_soul, "Charles", "Architect", ""), []);
        var view = new SoulProfileView { DataContext = empty };
        var window = HostWindow(view);
        window.Show();

        Assert.True(view.FindControl<TextBlock>("EmptyHistoryText")!.IsVisible);
        Assert.False(view.FindControl<Border>("ModelBadge")!.IsVisible);
        Assert.Equal(0, view.FindControl<ItemsControl>("HistoryList")!.ItemCount);
    }
}
