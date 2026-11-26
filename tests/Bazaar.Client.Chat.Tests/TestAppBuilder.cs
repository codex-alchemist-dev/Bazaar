using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Chat.Tests.TestAppBuilder))]

namespace Bazaar.Client.Chat.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
