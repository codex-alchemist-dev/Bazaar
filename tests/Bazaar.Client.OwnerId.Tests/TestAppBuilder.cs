using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Bazaar.Client.OwnerId.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bazaar.Client.OwnerId.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()));
}
