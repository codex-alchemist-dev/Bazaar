using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.Core;
using Bazaar.Client.Twin.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bazaar.Client.Twin.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
