using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.TimeMachine.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bazaar.Client.TimeMachine.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Bazaar.Client.Core.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
