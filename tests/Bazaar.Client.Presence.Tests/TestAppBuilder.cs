using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Presence.Tests.TestAppBuilder))]

namespace Bazaar.Client.Presence.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // Without a theme an ItemsControl has no template, so its items are never realized.
            .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()));
}
