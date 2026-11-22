using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.Core;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Windows.Tests.TestAppBuilder))]

namespace Bazaar.Client.Windows.Tests;

/// <summary>Builds the Avalonia application for headless tests: no display and no Windows are needed.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
