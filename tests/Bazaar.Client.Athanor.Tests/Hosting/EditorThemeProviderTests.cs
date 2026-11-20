using System.Text.Json.Nodes;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public class EditorThemeProviderTests
{
    [Fact]
    public void Pushed_json_equals_monaco_generator_output()
    {
        ThemeDefinition theme = HostingFixtures.DarkTheme();
        var provider = new EditorThemeProvider(() => theme);

        Assert.Equal(MonacoThemeGenerator.Generate(theme), provider.GetMonacoThemeJson());
    }

    [Fact]
    public void Pushed_json_has_the_monaco_shape_from_the_palette()
    {
        var provider = new EditorThemeProvider(() => HostingFixtures.DarkTheme());

        JsonObject root = JsonNode.Parse(provider.GetMonacoThemeJson())!.AsObject();

        Assert.Equal("vs-dark", root["base"]!.GetValue<string>());
        Assert.Equal("CBA6F7", root["rules"]![1]!["foreground"]!.GetValue<string>());
        Assert.Equal("#181825", root["colors"]!["editor.background"]!.GetValue<string>());
    }

    [Fact]
    public void Same_theme_instance_returns_the_cached_string()
    {
        ThemeDefinition theme = HostingFixtures.DarkTheme();
        var provider = new EditorThemeProvider(() => theme);

        string first = provider.GetMonacoThemeJson();
        string second = provider.GetMonacoThemeJson();

        Assert.Same(first, second);
    }

    [Fact]
    public void New_theme_instance_regenerates_the_json()
    {
        ThemeDefinition current = HostingFixtures.DarkTheme("#89B4FA");
        var provider = new EditorThemeProvider(() => current);
        string before = provider.GetMonacoThemeJson();

        current = HostingFixtures.DarkTheme("#F38BA8");
        string after = provider.GetMonacoThemeJson();

        Assert.NotEqual(before, after);
        Assert.Equal(MonacoThemeGenerator.Generate(current), after);
    }

    [Fact]
    public async Task Push_sends_the_theme_json_to_the_host_without_reloading()
    {
        ThemeDefinition theme = HostingFixtures.DarkTheme();
        var provider = new EditorThemeProvider(() => theme);
        await using var host = new PlainTextFallbackHost();

        await provider.PushAsync(host, CancellationToken.None);

        Assert.Equal(MonacoThemeGenerator.Generate(theme), host.ThemeJson);
        Assert.False(host.IsCrashed);
    }

    [Fact]
    public async Task Push_with_a_cancelled_token_does_not_reach_the_host()
    {
        var provider = new EditorThemeProvider(() => HostingFixtures.DarkTheme());
        await using var host = new PlainTextFallbackHost();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.PushAsync(host, cancellation.Token));

        Assert.Null(host.ThemeJson);
    }

    [Fact]
    public void Constructor_rejects_a_null_source()
    {
        Assert.Throws<ArgumentNullException>(() => new EditorThemeProvider(null!));
    }

    [Fact]
    public async Task Push_rejects_a_null_host()
    {
        var provider = new EditorThemeProvider(() => HostingFixtures.DarkTheme());

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.PushAsync(null!, CancellationToken.None));
    }
}
