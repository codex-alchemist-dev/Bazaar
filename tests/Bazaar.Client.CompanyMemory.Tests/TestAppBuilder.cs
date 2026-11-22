using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.CompanyMemory.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Bazaar.Client.CompanyMemory.Tests;

/// <summary>Builds the Avalonia application for headless tests: no display server is needed.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Bazaar.Client.Core.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
