using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.Athanor.AvaloniaEdit.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bazaar.Client.Athanor.AvaloniaEdit.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
