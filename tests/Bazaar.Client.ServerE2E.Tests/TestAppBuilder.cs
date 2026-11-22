using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.ServerE2E.Tests.TestAppBuilder))]

namespace Bazaar.Client.ServerE2E.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
