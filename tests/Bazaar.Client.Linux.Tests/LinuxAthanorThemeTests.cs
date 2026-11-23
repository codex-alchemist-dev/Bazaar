using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.AvaloniaEdit;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Linux.Tests;

public sealed class LinuxAthanorThemeTests
{
    private static string DataDir() => Path.Combine(Path.GetTempPath(), "bazaar-athanor-theme-tests", Guid.NewGuid().ToString("N"));

    private static ThemeDefinition WithEditorBackground(ThemeDefinition theme, string colour)
    {
        var tokens = new Dictionary<string, string>(theme.Tokens, StringComparer.Ordinal) { ["color.background.editor"] = colour };
        return theme with { Tokens = tokens };
    }

    [AvaloniaFact]
    public async Task The_head_pushes_the_app_theme_to_the_editor_and_a_swap_reaches_it_live()
    {
        var options = new AthanorHeadOptions { DataDirectory = DataDir(), Host = EditorHostKind.AvaloniaEdit };
        await using var services = AthanorHeadComposition.Create(options, null, AthanorClientModule.Commands, _ => { });
        var host = Assert.IsType<AvaloniaEditHost>(services.Host);

        var (theme, sync) = AthanorHeadComposition.StartTheme(options, services, _ => { });
        using var syncGuard = sync;

        await WaitForAsync(() => host.Colors is not null);
        Assert.Equal(Color.Parse("#181825"), host.Colors!.Background);

        theme.Set(WithEditorBackground(theme.Current, "#FFFFFF"));
        await WaitForAsync(() => host.Colors!.Background == Color.Parse("#FFFFFF"));

        Assert.Equal(Color.Parse("#FFFFFF"), host.Colors!.Background);
    }

    [AvaloniaFact]
    public async Task A_theme_file_in_the_data_folder_is_the_starting_theme()
    {
        var dir = DataDir();
        Directory.CreateDirectory(dir);
        using var stream = typeof(ClientThemeService).Assembly.GetManifestResourceStream(ClientThemeService.BuiltInDarkResource)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd()
            .Replace("\"Bazaar Dark\"", "\"Custom\"", StringComparison.Ordinal)
            .Replace("\"color.background.editor\": \"#181825\"", "\"color.background.editor\": \"#101010\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, "theme.json"), json);
        var options = new AthanorHeadOptions { DataDirectory = dir, Host = EditorHostKind.AvaloniaEdit };
        await using var services = AthanorHeadComposition.Create(options, null, AthanorClientModule.Commands, _ => { });
        var host = Assert.IsType<AvaloniaEditHost>(services.Host);

        var log = new List<string>();
        var (theme, sync) = AthanorHeadComposition.StartTheme(options, services, log.Add);
        using var syncGuard = sync;
        await WaitForAsync(() => host.Colors is not null);

        Assert.True(theme.Current.Name == "Custom", string.Join("; ", log));
        Assert.Equal(Color.Parse("#101010"), host.Colors!.Background);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
