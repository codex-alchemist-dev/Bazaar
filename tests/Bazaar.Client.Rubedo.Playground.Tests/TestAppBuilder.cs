using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Bazaar.Client.Rubedo.Playground.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bazaar.Client.Rubedo.Playground.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::Avalonia.Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // Without a theme the templated controls (ComboBox, NumericUpDown, ListBox) have no template.
            .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()));
}
