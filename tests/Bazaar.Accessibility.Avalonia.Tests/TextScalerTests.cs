using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Bazaar.Accessibility.Avalonia.Tests;

public class TextScalerTests
{
    [AvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(9.0, 3.0)]
    [InlineData(double.NaN, 1.0)]
    public void The_scale_is_clamped(double given, double expected)
    {
        var scaler = new TextScaler();

        scaler.SetScale(given);

        Assert.Equal(expected, scaler.Scale);
    }

    [AvaloniaFact]
    public void Doubling_the_scale_doubles_the_measured_content()
    {
        var scaler = new TextScaler();
        scaler.SetContent(new Border { Width = 100, Height = 40 });
        var window = new Window { Content = scaler.Host };
        window.Show();
        scaler.Host.Measure(new global::Avalonia.Size(2000, 2000));
        var normal = scaler.Host.DesiredSize;

        scaler.SetScale(2.0);
        scaler.Host.Measure(new global::Avalonia.Size(2000, 2000));

        Assert.Equal(normal.Width * 2, scaler.Host.DesiredSize.Width, 3);
        Assert.Equal(normal.Height * 2, scaler.Host.DesiredSize.Height, 3);
    }

    [AvaloniaFact]
    public void The_chrome_hosts_content_under_the_scaler()
    {
        var chrome = WindowChrome.Attach(new Window(), new Bazaar.Localisation.SystemLocaleSource("en"), new AnnouncementGate(TimeProvider.System));
        var content = new TextBlock { Text = "hi" };

        chrome.Wrap(content);
        chrome.TextScale.SetScale(2.0);

        Assert.Same(content, chrome.TextScale.Host.Child);
        Assert.Equal(2.0, chrome.TextScale.Scale);
    }
}
