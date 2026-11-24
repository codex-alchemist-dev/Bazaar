using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace S12;

internal static class Program
{
    public static string[] Args = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            d.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
