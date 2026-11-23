using Bazaar.Client.Theme;

namespace Bazaar.Client.Theme.Tests;

public class ClientThemeServiceTests
{
    private static string TempFile(string? content)
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-theme-tests", Guid.NewGuid().ToString("N") + ".json");
        if (content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return path;
    }

    [Fact]
    public void The_built_in_dark_theme_validates_and_is_dark()
    {
        var theme = ClientThemeService.BuiltInDark();

        Assert.Equal(ThemeBase.Dark, theme.Base);
        Assert.Equal("Bazaar Dark", theme.Name);
    }

    [Fact]
    public void A_missing_file_starts_from_the_built_in_theme_silently()
    {
        var log = new List<string>();

        var service = ClientThemeService.Create(TempFile(null), log.Add);

        Assert.Equal("Bazaar Dark", service.Current.Name);
        Assert.Empty(log);
    }

    [Fact]
    public void A_valid_file_wins_over_the_built_in_theme()
    {
        var service = ClientThemeService.Create(TempFile(ThemeFixtures.Read("light.theme.json")), _ => { });

        Assert.Equal(ThemeBase.Light, service.Current.Base);
    }

    [Fact]
    public void An_invalid_file_is_reported_and_the_built_in_theme_is_used()
    {
        var log = new List<string>();

        var service = ClientThemeService.Create(TempFile("{ \"schemaVersion\": 1 }"), log.Add);

        Assert.Equal("Bazaar Dark", service.Current.Name);
        Assert.Single(log);
    }

    [Fact]
    public void Set_changes_the_theme_and_raises_Changed_once_per_new_instance()
    {
        var service = new ClientThemeService(ClientThemeService.BuiltInDark());
        var next = ClientThemeService.BuiltInDark();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Set(next);
        service.Set(next);

        Assert.Same(next, service.Current);
        Assert.Equal(1, raised);
    }
}
