using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Devices.Tests.TestAppBuilder))]

namespace Bazaar.Client.Devices.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Avalonia.Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
