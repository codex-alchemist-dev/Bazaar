using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Core;

namespace Bazaar.Client.Linux.Tests;

/// <summary>
/// Headless smoke test for the Linux head's window. It runs without a display server (Avalonia.Headless), so CI needs no Xvfb.
/// It checks that the shared main window builds, shows and closes on the Linux head without throwing.
/// </summary>
public class LinuxShellHeadlessTests
{
    [AvaloniaFact]
    public void Main_window_opens_and_closes_without_settings()
    {
        // Program.CreateMainWindow builds this same window when no connection settings are present.
        var window = new MainWindow();

        window.Show();
        Assert.True(window.IsVisible);

        window.Close();
        Assert.False(window.IsVisible);
    }
}
