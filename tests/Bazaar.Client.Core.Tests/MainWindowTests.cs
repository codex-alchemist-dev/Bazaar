using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Core.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_has_expected_title()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("Bazaar", window.Title);
    }

    [AvaloniaFact]
    public void MainWindow_renders_a_frame_to_png()
    {
        var window = new MainWindow();
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var path = Path.Combine(AppContext.BaseDirectory, "MainWindow.png");
        frame.Save(path, new PngBitmapEncoderOptions());

        Assert.True(new FileInfo(path).Length > 0);
    }
}
