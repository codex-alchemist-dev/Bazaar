using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Core;

namespace Bazaar.Client.Core.Tests;

/// <summary>The main window takes its start content as a plain control; a head builds the chat view and passes it in.</summary>
public class MainWindowContentTests
{
    [AvaloniaFact]
    public void Main_window_without_content_keeps_the_plain_start_view()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("Bazaar", window.Title);
        Assert.Equal("Bazaar", Assert.IsType<TextBlock>(window.Content).Text);
    }

    [AvaloniaFact]
    public void Main_window_shows_the_content_it_is_given()
    {
        var content = new TextBlock { Text = "start" };

        var window = new MainWindow(content);
        window.Show();

        Assert.Same(content, window.Content);
    }
}
