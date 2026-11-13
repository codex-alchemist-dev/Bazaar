using Bazaar.Client.Core.Preload;
using Bazaar.Client.Windows;

namespace Bazaar.Client.Windows.Tests;

/// <summary>
/// Headless platform smoke tests. They never show a window and never touch the real Run key.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsPlatformSmokeTests
{
    [Fact]
    public void PreloadRunEntry_command_names_the_executable()
    {
        var command = PreloadRunEntry.Command(@"C:\Program Files\Bazaar\Bazaar.Client.Windows.exe");

        Assert.Contains("Bazaar.Client.Windows.exe", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preload_run_key_is_per_user()
    {
        Assert.StartsWith(@"Software\", PreloadRunEntry.RunKeyPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Autostart_implementation_is_constructible_without_side_effects()
    {
        IPreloadAutostart autostart = new WindowsPreloadAutostart();

        Assert.NotNull(autostart);
    }
}
