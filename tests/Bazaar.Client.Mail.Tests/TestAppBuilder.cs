using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Mail.Tests.TestAppBuilder))]

namespace Bazaar.Client.Mail.Tests;

/// <summary>Builds the Avalonia application for headless tests: no display server is needed.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
