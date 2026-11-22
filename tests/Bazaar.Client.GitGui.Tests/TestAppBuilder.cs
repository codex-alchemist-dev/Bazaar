using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.GitGui.Tests.TestAppBuilder))]

namespace Bazaar.Client.GitGui.Tests;

/// <summary>Builds the Avalonia application for headless tests: no display server is needed.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>The test application. It loads the Fluent theme so list controls have a template, as every head does.</summary>
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
