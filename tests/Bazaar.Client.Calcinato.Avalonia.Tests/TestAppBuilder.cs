using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.Calcinato.Avalonia.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// The headless tests share one application's theme resources, so they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bazaar.Client.Calcinato.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::Avalonia.Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
